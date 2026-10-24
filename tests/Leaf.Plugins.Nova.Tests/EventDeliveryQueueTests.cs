using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 258167da: an event accepted by POST /discussions/{id}/event reaches the discussion's session
/// exactly once, in order with its other inputs, also when the session is still being created,
/// deferred, or was interrupted by a restart.
/// </summary>
public sealed class EventDeliveryQueueTests
{
    [Fact]
    public async Task Event_posted_while_the_session_is_being_created_is_delivered_once_after_it_exists()
    {
        using var w = new World(sessionId: null);
        w.Gateway.CreationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The repro: create (which starts the session in the background), then the event at once.
        w.Pipeline.BeginSessionCreation(w.Discussion());
        var posting = w.PostEventAsync("Hello from the race repro");

        await w.WaitUntilAsync(() => w.Queue().Count == 1);
        Assert.Empty(w.Gateway.Messages);
        // A callback reaching the same discussion meanwhile queues behind it, not dropped.
        Assert.False(await w.Injector.InjectAsync(w.Discussion(), "Second event", null, "callback"));
        Assert.Equal(2, w.Queue().Count);

        w.Gateway.CreationGate.SetResult();
        var (status, body) = await posting;

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        Assert.Equal(1, w.Gateway.SessionsCreated);
        Assert.Equal(new[] { "Hello from the race repro", "Second event" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.All(w.Gateway.Messages, m => Assert.Equal("session-1", m.Session));
        Assert.Equal(body.GetProperty("messageUid").GetString(), w.Gateway.Messages[0].MessageUid);
        Assert.Equal(w.EventRecords().Select(r => r.Metadata["uid"]!.GetValue<string>()),
            w.Gateway.Messages.Select(m => m.MessageUid));
        Assert.Empty(w.Queue());
        Assert.Equal("session-1", w.Discussion().SessionId);

        // Nothing re-injects them: a later drain and the first user message add only that message.
        await w.Injector.DeliverPendingAsync(w.Entity.Id);
        Assert.True((await w.SendAsync("first reply")).Success);
        Assert.Equal(3, w.Gateway.Messages.Count);
        Assert.Contains("first reply", w.Gateway.Messages[2].Text);
    }

    [Fact]
    public async Task Deferred_discussion_delivers_queued_events_ahead_of_the_first_user_message_in_order()
    {
        using var w = new World(sessionId: null);

        var first = await w.PostEventAsync("Story: first event");
        var second = await w.PostEventAsync("Story: second event");

        foreach (var (status, body) in new[] { first, second })
        {
            Assert.Equal(202, status);
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.Equal("queued", body.GetProperty("delivery").GetString());
        }
        // An event never forces the deferred session into existence.
        Assert.Equal(0, w.Gateway.SessionsCreated);
        Assert.Empty(w.Gateway.Messages);
        Assert.Equal(2, w.Queue().Count);
        Assert.True(w.Entity.Data[PendingEventDeliveries.PendingFlagKey]!.GetValue<bool>());

        var sent = await w.SendAsync("hello Nova");

        Assert.True(sent.Success, sent.ErrorMessage);
        Assert.Equal(1, w.Gateway.SessionsCreated);
        Assert.Equal(3, w.Gateway.Messages.Count);
        Assert.Equal("Story: first event", w.Gateway.Messages[0].Text);
        Assert.Equal("Story: second event", w.Gateway.Messages[1].Text);
        Assert.Contains("hello Nova", w.Gateway.Messages[2].Text);
        Assert.Equal(first.Body.GetProperty("messageUid").GetString(), w.Gateway.Messages[0].MessageUid);
        Assert.Equal(second.Body.GetProperty("messageUid").GetString(), w.Gateway.Messages[1].MessageUid);
        Assert.Empty(w.Queue());
        Assert.False(w.Entity.Data[PendingEventDeliveries.PendingFlagKey]!.GetValue<bool>());

        // The next message does not deliver them again.
        Assert.True((await w.SendAsync("again")).Success);
        Assert.Equal(4, w.Gateway.Messages.Count);
    }

    [Fact]
    public async Task Restart_while_queued_delivers_after_restart_once()
    {
        using var w = new World(sessionId: null);
        var (status, _) = await w.PostEventAsync("Queued before the restart");
        Assert.Equal(202, status);
        // The session was created and recorded, then the process died before its drain ran.
        w.Store.SetSession("session-7");

        using var restarted = w.Restart();
        await restarted.Injector.DeliverAllPendingAsync(CancellationToken.None);

        var delivered = Assert.Single(w.Gateway.Messages);
        Assert.Equal("session-7", delivered.Session);
        Assert.Equal("Queued before the restart", delivered.Text);
        Assert.Empty(restarted.Queue());

        // A second sweep (another restart) finds nothing to deliver.
        using var again = restarted.Restart();
        await again.Injector.DeliverAllPendingAsync(CancellationToken.None);
        Assert.Single(w.Gateway.Messages);
    }

    [Fact]
    public async Task Restart_while_queued_without_a_session_keeps_the_event_for_the_first_message()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("Queued, deferred, restarted")).Status);

        using var restarted = w.Restart();
        await restarted.Injector.DeliverAllPendingAsync(CancellationToken.None);
        Assert.Empty(w.Gateway.Messages);
        Assert.Equal(0, w.Gateway.SessionsCreated);

        Assert.True((await restarted.SendAsync("hi")).Success);
        Assert.Equal("Queued, deferred, restarted", w.Gateway.Messages[0].Text);
        Assert.Contains("hi", w.Gateway.Messages[1].Text);
        Assert.Equal(2, w.Gateway.Messages.Count);
    }

    [Fact]
    public async Task An_event_admitted_before_its_acknowledgement_was_lost_is_not_reinjected_on_drain()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("Admitted, reply lost")).Status);
        w.Store.SetSession("session-3");
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;

        var interrupted = await w.Injector.DeliverPendingAsync(w.Entity.Id);
        Assert.Equal(EventDrainStatus.Failed, interrupted.Status);
        Assert.Single(w.Queue()); // RedCompute holds it, but Nova could not know.

        using var restarted = w.Restart();
        await restarted.Injector.DeliverAllPendingAsync(CancellationToken.None);

        Assert.Single(w.Gateway.Messages);
        Assert.Equal(2, w.Gateway.Attempts.Count);
        Assert.NotNull(w.Gateway.Attempts[0].Key);
        Assert.Equal(w.Gateway.Attempts[0].Key, w.Gateway.Attempts[1].Key);
        Assert.Empty(restarted.Queue());
    }

    [Fact]
    public async Task An_event_delivered_directly_is_never_queued_or_drained_again()
    {
        using var w = new World(sessionId: "session-live");
        var (status, body) = await w.PostEventAsync("Straight in");

        Assert.Equal(200, status);
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        Assert.Empty(w.Queue());
        await w.Injector.DeliverPendingAsync(w.Entity.Id);
        using var restarted = w.Restart();
        await restarted.Injector.DeliverAllPendingAsync(CancellationToken.None);
        Assert.True((await restarted.SendAsync("next")).Success);

        Assert.Equal(2, w.Gateway.Messages.Count);
        Assert.Equal("Straight in", w.Gateway.Messages[0].Text);
        Assert.Contains("next", w.Gateway.Messages[1].Text);
    }

    [Fact]
    public async Task A_later_event_waits_behind_earlier_queued_ones_instead_of_overtaking_them()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("older")).Status);
        // The session appears (e.g. a voice call created it); the next event drains in order.
        w.Store.SetSession("session-5");

        var (status, _) = await w.PostEventAsync("newer");

        Assert.Equal(200, status);
        Assert.Equal(new[] { "older", "newer" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.Empty(w.Queue());
    }

    [Fact]
    public async Task Undelivered_event_is_not_reported_as_success_and_a_keyed_retry_delivers_once()
    {
        using var w = new World(sessionId: "session-live");
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;

        var (failedStatus, failed) = await w.PostEventAsync("Retry me", idempotencyKey: "story-42");
        Assert.Equal(502, failedStatus);
        Assert.False(failed.GetProperty("success").GetBoolean());
        Assert.True(failed.GetProperty("retryable").GetBoolean());

        var (retryStatus, retried) = await w.PostEventAsync("Retry me", idempotencyKey: "story-42");
        Assert.Equal(200, retryStatus);
        Assert.Equal(failed.GetProperty("messageUid").GetString(), retried.GetProperty("messageUid").GetString());
        Assert.Single(w.EventRecords());
        Assert.Single(w.Gateway.Messages);
        Assert.Equal(w.Gateway.Attempts[0].Key, w.Gateway.Attempts[1].Key);
    }

    [Fact]
    public async Task Refused_event_is_reported_as_undeliverable()
    {
        using var w = new World(sessionId: "session-live");
        w.Gateway.RefuseMessages = true;

        var (status, body) = await w.PostEventAsync("Too much");

        Assert.Equal(422, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Refused_queued_event_leaves_the_queue_and_does_not_block_the_first_message()
    {
        using var w = new World(sessionId: null);
        var (_, queued) = await w.PostEventAsync("Refused later");
        w.Gateway.RefuseMessages = true;
        w.Gateway.RefuseOnlyEvents = true;

        Assert.True((await w.SendAsync("hello")).Success);

        Assert.Empty(w.Queue());
        var state = await w.Injector.QueuedStateAsync(w.Entity.Id, queued.GetProperty("messageUid").GetString()!);
        Assert.Equal(EventDelivery.Undeliverable, state.Delivery);
        Assert.Contains("hello", Assert.Single(w.Gateway.Messages).Text);
    }

    [Fact]
    public async Task User_message_is_not_sent_ahead_of_an_event_that_could_not_be_delivered_yet()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("Must go first")).Status);
        w.Store.SetSession("session-2");
        w.Gateway.FailNextMessages = 1;

        var sent = await w.SendAsync("after the event");

        Assert.False(sent.Success);
        Assert.Empty(w.Gateway.Messages);
        Assert.Single(w.Queue());

        Assert.True((await w.SendAsync("after the event")).Success);
        Assert.Equal("Must go first", w.Gateway.Messages[0].Text);
        Assert.Contains("after the event", w.Gateway.Messages[1].Text);
    }

    [Fact]
    public async Task Assistant_typed_event_is_queued_and_delivered_like_any_other()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("Posted as the assistant", type: "assistant")).Status);

        Assert.True((await w.SendAsync("hi")).Success);

        Assert.Equal("Posted as the assistant", w.Gateway.Messages[0].Text);
        Assert.Equal(2, w.Gateway.Messages.Count);
        Assert.Empty(w.Queue());
    }

    [Fact]
    public async Task System_events_stay_display_only()
    {
        using var w = new World(sessionId: null);
        var (status, body) = await w.PostEventAsync("Timeline note", type: "system");

        Assert.Equal(200, status);
        Assert.Equal("recorded", body.GetProperty("delivery").GetString());
        Assert.Empty(w.Queue());
        Assert.True((await w.SendAsync("hi")).Success);
        Assert.Single(w.Gateway.Messages);
    }

    [Fact]
    public async Task Concurrent_sends_during_session_creation_share_the_one_session()
    {
        using var w = new World(sessionId: null);
        w.Gateway.CreationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Pipeline.BeginSessionCreation(w.Discussion());

        var sends = new[] { w.SendAsync("one"), w.SendAsync("two") };
        await w.WaitUntilAsync(() => w.Gateway.SessionsRequested >= 1);
        w.Gateway.CreationGate.SetResult();
        var results = await Task.WhenAll(sends);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        Assert.Equal(1, w.Gateway.SessionsCreated);
        Assert.All(w.Gateway.Messages, m => Assert.Equal("session-1", m.Session));
    }

    private sealed class World : IDisposable
    {
        private readonly AgentDirectory agents;
        private readonly string scratch;
        private readonly bool ownsScratch;

        public EntityStore Store { get; }
        public MessageStream Stream { get; }
        public Gateway Gateway { get; }
        public DiscussionStore Discussions { get; }
        public EventInjector Injector { get; }
        public MessagePipeline Pipeline { get; }
        public LeafEntity Entity => Store.Get(Store.DiscussionId);

        public World(string? sessionId) : this(EntityStore.Create(sessionId), new MessageStream(), new Gateway(), null) { }

        private World(EntityStore store, MessageStream stream, Gateway gateway, string? sharedScratch)
        {
            Store = store;
            Stream = stream;
            Gateway = gateway;
            ownsScratch = sharedScratch is null;
            scratch = sharedScratch ?? Path.Combine(Path.GetTempPath(), $"nova-event-queue-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            var events = CallbackDeliveryTests.Proxy.Create<IPluginEvents>((method, _) => method == "Subscribe" ? new Noop() : Task.CompletedTask);
            agents = new AgentDirectory(store, events) { NovaAgentId = store.AgentId.ToString() };
            var client = new RedComputeClient(gateway);
            Discussions = new DiscussionStore(store, stream);
            var unread = new ConversationUnread(Discussions, stream, client);
            Injector = new EventInjector(stream, events, client, agents, store, unread);
            var extensions = CallbackDeliveryTests.Proxy.Create<IPluginExtensions>((method, _) => method switch
            {
                "CollectContextAsync" => Task.FromResult<IReadOnlyList<PluginContextFragment>>([]),
                _ => new Noop(),
            });
            Pipeline = new MessagePipeline(Discussions, stream, store, null!, client, agents,
                new AgentWorkspaces(agents, new NoWorkspace()), new Scratch(scratch),
                new ExtensionContributions(extensions, new LiveEvents(Discussions, Injector, agents)),
                unread, NullLogger<MessagePipeline>.Instance, eventInjector: Injector);
        }

        /// <summary>A new process over the same durable state: no in-memory queue, locks or pending creations.</summary>
        public World Restart() => new(Store, Stream, Gateway, scratch);

        public DiscussionRead Discussion() => DiscussionStore.Map(Entity)!;

        public List<PendingEventDelivery> Queue() => PendingEventDeliveries.Read(Entity.Data);

        public IEnumerable<DiscussionMessage> EventRecords()
            => Stream.Snapshot().Where(r => r.Metadata["source"]?.GetValue<string>()?.StartsWith("event:") == true);

        public async Task<(int Status, JsonElement Body)> PostEventAsync(string content, string? type = null, string? idempotencyKey = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            var result = await DiscussionEndpoints.PostEventAsync(Discussion().Id,
                new DiscussionEventRequest { Content = content, Type = type, Source = "insights", IdempotencyKey = idempotencyKey },
                ctx, Discussions, agents, Injector, Pipeline);
            return (((IStatusCodeHttpResult)result).StatusCode ?? 200,
                JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value));
        }

        public Task<SendMessageOutcome> SendAsync(string content)
            => Pipeline.SendAsync(Discussion(), null, content, null,
                new ResolvedDevice { Name = "test", Type = "desktop", Platform = "test" }, "text");

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached");
                await Task.Delay(10);
            }
        }

        public void Dispose()
        {
            agents.Dispose();
            if (ownsScratch)
                try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    /// <summary>The kernel entity store: shallow patches, top-level JSON equality filters.</summary>
    private sealed class EntityStore : IEntityStore
    {
        private readonly List<LeafEntity> entities;
        public Guid AgentId { get; }
        public Guid DiscussionId { get; }

        private EntityStore(List<LeafEntity> entities, Guid agentId, Guid discussionId)
        {
            this.entities = entities;
            AgentId = agentId;
            DiscussionId = discussionId;
        }

        public static EntityStore Create(string? sessionId)
        {
            var agent = New("agent", "nova", new JsonObject());
            var discussion = New("discussion", DiscussionStore.Slug("race1234"), new JsonObject
            {
                ["discussion_id"] = "race1234", ["app"] = "nova", ["type"] = "chat", ["status"] = "idle",
                ["agent"] = agent.Id.ToString(), ["owner_id"] = "local-user", ["session_id"] = sessionId,
                ["conversation_revision"] = 0, ["read_conversation_revision"] = 0,
                ["last_processed_session_assistant_uid"] = "",
            });
            return new([agent, discussion, New("plugin", NovaAppPlugin.PluginId, new JsonObject())], agent.Id, discussion.Id);
        }

        public LeafEntity Get(Guid id) { lock (entities) return entities.Single(e => e.Id == id); }

        public void SetSession(string sessionId) => PatchAsync(DiscussionId, new JsonObject { ["session_id"] = sessionId }).Wait();

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
        { lock (entities) return Task.FromResult(entities.FirstOrDefault(e => e.Id == id)); }

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
        { lock (entities) return Task.FromResult(entities.FirstOrDefault(e => e.Slug == slug)); }

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            lock (entities)
            {
                IEnumerable<LeafEntity> rows = entities.Where(e => e.TypeSlug == query.TypeSlug);
                foreach (var (key, value) in query.DataEquals ?? new Dictionary<string, object?>())
                    rows = rows.Where(e => JsonNode.DeepEquals(e.Data[key], JsonSerializer.SerializeToNode(value)));
                if (query.OrderById) rows = rows.OrderBy(e => e.Id);
                if (query.AfterId is { } after) rows = rows.Where(e => e.Id.CompareTo(after) > 0);
                return Task.FromResult<IReadOnlyList<LeafEntity>>(rows.Skip(query.Offset).Take(query.Limit).ToList());
            }
        }

        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
        {
            lock (entities)
            {
                var index = entities.FindIndex(e => e.Id == id);
                var data = entities[index].Data.DeepClone().AsObject();
                foreach (var (key, value) in dataPatch) data[key] = value?.DeepClone();
                entities[index] = entities[index] with { Data = data, Name = name ?? entities[index].Name, UpdatedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(entities[index]);
            }
        }

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        private static LeafEntity New(string type, string slug, JsonObject data)
            => new(Guid.NewGuid(), type, slug, slug, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
    }

    private sealed class MessageStream : IDiscussions
    {
        private readonly List<DiscussionMessage> records = [];

        public DiscussionMessage[] Snapshot() { lock (records) return records.ToArray(); }

        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default)
        {
            lock (records) records.Add(new(records.Count + 1, discussionId, role, content, metadata ?? new JsonObject(), DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default)
        {
            await Task.Yield();
            lock (records)
                return records.Where(r => r.DiscussionId == discussionId && r.Id > afterId).OrderBy(r => r.Id).Take(limit).ToArray();
        }

        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new Noop();
    }

    /// <summary>RedCompute's session API: durable admission deduplicated by X-Idempotency-Key.</summary>
    private sealed class Gateway : IComputeGateway
    {
        private readonly Dictionary<string, string> admittedKeys = [];
        public TaskCompletionSource? CreationGate { get; set; }
        public int SessionsRequested;
        public int SessionsCreated;
        public int FailNextMessages { get; set; }
        public bool AdmitBeforeFailure { get; set; }
        public bool RefuseMessages { get; set; }
        public bool RefuseOnlyEvents { get; set; }
        public List<Admitted> Messages { get; } = [];
        public List<(string Session, string? Key)> Attempts { get; } = [];

        public sealed record Admitted(string Session, string Text, string? MessageUid, string? Key);

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri!.OriginalString;
            if (request.Method == HttpMethod.Post && path == "/ai-session/sessions")
            {
                var number = Interlocked.Increment(ref SessionsRequested);
                if (CreationGate is { } gate) await gate.Task;
                Interlocked.Increment(ref SessionsCreated);
                return Json(HttpStatusCode.OK, $$"""{"id":"session-{{number}}"}""");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/message", StringComparison.Ordinal))
            {
                Assert.NotNull(provenance);
                var session = path.Split('/')[3];
                var key = request.Headers.TryGetValues("X-Idempotency-Key", out var values) ? values.Single() : null;
                var raw = await request.Content!.ReadAsStringAsync(ct);
                using var body = JsonDocument.Parse(raw);
                var content = body.RootElement.GetProperty("content").GetString()!;
                var isEvent = content.StartsWith("<nova-event", StringComparison.Ordinal);
                var text = isEvent ? content.Split('\n')[1] : content;
                var uid = body.RootElement.TryGetProperty("messageUid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                lock (Messages)
                {
                    Attempts.Add((session, key));
                    if (RefuseMessages && (isEvent || !RefuseOnlyEvents))
                        return Json(HttpStatusCode.UnprocessableEntity, """{"error":"content_too_large","message":"refused"}""");
                    var fail = FailNextMessages > 0;
                    if (fail) FailNextMessages--;
                    if (!fail || AdmitBeforeFailure)
                    {
                        if (key is not null && admittedKeys.TryGetValue(key, out var original))
                        {
                            if (original != raw) return Json(HttpStatusCode.Conflict, """{"error":"idempotency_conflict"}""");
                        }
                        else
                        {
                            if (key is not null) admittedKeys.Add(key, raw);
                            Messages.Add(new(session, text, uid, key));
                        }
                    }
                    if (fail) throw new HttpRequestException("Simulated lost acknowledgement");
                }
                return Json(HttpStatusCode.OK, $$"""{"disposition":"delivered","messageUid":"{{uid ?? Guid.NewGuid().ToString("N")}}"}""");
            }
            if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, "{}");
            return Json(HttpStatusCode.NotFound, """{"error":"not_found"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body) };
    }

    private sealed class NoWorkspace : IAgentWorkspacePathResolver
    {
        public Task<string?> ResolveAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class Scratch(string path) : IAgentScratchSpace
    {
        public AgentScratchAllocation PrepareExecution(string owner, string executionKey) => new(path, new Dictionary<string, string>());
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
}
