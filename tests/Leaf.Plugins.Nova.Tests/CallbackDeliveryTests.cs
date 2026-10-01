using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class CallbackDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReplyAfterAcceptanceReusesTargetAdmissionAndLiveSummary(bool confidential)
    {
        using var f = new Fixture(confidential: confidential);
        Assert.Equal(200, await f.Complete("prompt-one"));
        // The caller did not see that reply and retries the same callback.
        Assert.Equal(200, await f.Complete("prompt-one"));
        Assert.Single(f.TargetMessages);
        Assert.Single(f.Gateway.Admitted);
        Assert.Equal(confidential ? 0 : 1, f.LiveMessages.Count());
        Assert.Equal(2, f.Gateway.Attempts);
        Assert.Equal(200, await f.Complete("prompt-two"));
        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Equal(2, f.Gateway.Admitted.Count);
        Assert.Equal(confidential ? 0 : 2, f.LiveMessages.Count());
        Assert.Equal(2, f.TargetMessages.Select(m => m.Metadata["uid"]!.GetValue<string>()).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryFinishesAdmissionAfterPersistenceWithoutAcknowledgingLostWork(bool acceptedBeforeFailure)
    {
        using var f = new Fixture();
        f.Gateway.FailNext = true;
        f.Gateway.AcceptBeforeFailure = acceptedBeforeFailure;
        Assert.Equal(502, await f.Complete("retry-prompt"));
        Assert.Single(f.TargetMessages);
        Assert.Empty(f.LiveMessages);
        Assert.Equal(200, await f.Complete("retry-prompt"));
        Assert.Single(f.TargetMessages);
        Assert.Single(f.LiveMessages);
        Assert.Single(f.Gateway.Admitted);
    }

    [Fact]
    public async Task DuplicateBeyondFirstPageAndConcurrentRetriesPersistOnce()
    {
        using var f = new Fixture(sessionless: true);
        for (var i = 0; i < 1001; i++)
            f.Records.Add(new(i + 1, f.Target.Id, "system", "earlier", new(), DateTimeOffset.UtcNow));
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Complete("one-completion")));
        Assert.All(statuses, status => Assert.Equal(200, status));
        Assert.Single(f.TargetMessages, m => m.Metadata.ContainsKey("idempotency_key"));
        Assert.Single(f.LiveMessages);
        Assert.Contains(f.PageReads, after => after >= 1000);
    }

    [Fact]
    public async Task MissingIdentityKeepsLegacyCompletionsDistinctAndInvalidIdentityIsRejected()
    {
        using var f = new Fixture();
        f.Gateway.FailNext = true; // Preserve old acknowledgement policy without an identity.
        Assert.Equal(200, await f.Complete(null));
        Assert.Equal(200, await f.Complete(null));
        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Equal(400, await f.Complete(""));
        Assert.Equal(400, await f.Complete(new string('x', 201)));
        Assert.Equal(2, f.TargetMessages.Count());
    }

    [Fact]
    public async Task MaximumCallbackIdentityFitsTheAdmissionKeyContract()
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete(new string('x', 200)));
        Assert.Equal(200, await f.Complete(new string('x', 200)));
        Assert.Single(f.Gateway.Admitted);
    }

    [Fact]
    public async Task UnkeyedInternalEventsAndHeartbeatRedeliveryKeepExistingAdmissionPolicy()
    {
        using var f = new Fixture();
        var discussion = (await f.Store.GetAsync("target"))!;
        Assert.True(await f.Injector.InjectAsync(discussion, "tick", null, "heartbeat", idempotencyKey: "tick", redeliverOnReuse: true));
        Assert.True(await f.Injector.InjectAsync(discussion, "tick", null, "heartbeat", idempotencyKey: "tick", redeliverOnReuse: true));
        Assert.Single(f.TargetMessages);
        Assert.Equal(2, f.Gateway.Attempts);
        Assert.All(f.Gateway.Keys, key => Assert.Null(key));
    }

    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public async Task RestartPauseIsTruthfulAndDoesNotDeduplicateLaterCompletion(string reason)
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete("prompt-one", "Stopped", reason));
        Assert.Equal(200, await f.Complete("prompt-one", "Stopped", reason));
        Assert.Single(f.TargetMessages);
        Assert.Contains("paused for restart", f.TargetMessages.Single().Content);
        Assert.Contains("waiting to resume", f.LiveMessages.Single().Content);
        Assert.DoesNotContain("completed", f.LiveMessages.Single().Content);
        Assert.Equal(200, await f.Complete("prompt-one"));
        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Equal(2, f.Gateway.Admitted.Count);
        Assert.Equal(2, f.LiveMessages.Count());
        Assert.Contains(f.LiveMessages, message => message.Content.Contains("completed"));
        // A delayed duplicate pause cannot replace either a newer callback or
        // the old callback's actual completion; the projection uses live state.
        Assert.Equal(200, await f.Complete("prompt-two"));
        Assert.Equal(200, await f.Complete("prompt-one", "Stopped", reason));
        Assert.Equal(3, f.TargetMessages.Count());
    }

    [Theory]
    [InlineData("Error", null, "failed")]
    [InlineData("Stopped", "user_stopped", "stopped")]
    public async Task LiveSummaryDoesNotCallFailureOrExplicitStopCompleted(string status, string? reason, string outcome)
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete("prompt-one", status, reason));
        Assert.Contains($"Delegated session {outcome}", f.LiveMessages.Single().Content);
        Assert.DoesNotContain("completed", f.LiveMessages.Single().Content);
    }

    [Theory]
    [InlineData("completed", "prompt-one", null, "completed")]
    [InlineData("completed", null, null, "ended")]
    [InlineData("completed", "older-prompt", null, "ended")]
    [InlineData("terminated", null, null, "ended")]
    [InlineData(null, null, null, "ended")]
    [InlineData("failed", null, null, "failed")]
    [InlineData("completed", "prompt-one", "maintenance_restart", "paused")]
    public async Task EndedOnlyMeansCompletedWithExactPromptDeliveryEvidence(
        string? reason, string? deliveredUid, string? stopReason, string expected)
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete("prompt-one", "Ended", stopReason, reason, deliveredUid, "prompt-one", deliveredUid == "prompt-one"));
        Assert.Contains($"Delegated session {expected}", f.LiveMessages.Single().Content);
        if (expected != "completed") Assert.DoesNotContain("Delegated session completed", f.LiveMessages.Single().Content);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("superseded")]
    public async Task CancelledInputNeverReportsCompletion(string reason)
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete("operation", "Cancelled", reason: reason, promptMessageUid: "prompt"));
        Assert.Contains($"Delegated session {reason}", f.LiveMessages.Single().Content);
        Assert.DoesNotContain("Delegated session completed", f.LiveMessages.Single().Content);
    }

    [Fact]
    public async Task MergedPromptCompletionPreservesSeparateOperationPromptAndDeliveryIdentity()
    {
        using var f = new Fixture();
        Assert.Equal(200, await f.Complete("operation", "Ended", reason: "completed", deliveredMessageUid: "batch-first", promptMessageUid: "batch-second", executionObserved: true));
        Assert.Contains("Delegated session completed", f.LiveMessages.Single().Content);
        var record = f.TargetMessages.Single();
        using var parts = JsonDocument.Parse(record.Metadata["parts_json"]!.GetValue<string>());
        var data = parts.RootElement.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "event_data").GetProperty("data");
        Assert.Equal("batch-second", data.GetProperty("promptMessageUid").GetString());
        Assert.Equal("batch-first", data.GetProperty("deliveredMessageUid").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        public List<DiscussionMessage> Records { get; } = [];
        public List<long> PageReads { get; } = [];
        public Gateway Gateway { get; } = new();
        public LeafEntity Target { get; }
        private readonly LeafEntity live;
        private readonly AgentDirectory agents;
        public DiscussionStore Store { get; }
        public EventInjector Injector { get; }
        private readonly LiveEvents liveEvents;
        public IEnumerable<DiscussionMessage> TargetMessages => Records.Where(m => m.DiscussionId == Target.Id);
        public IEnumerable<DiscussionMessage> LiveMessages => Records.Where(m => m.DiscussionId == live.Id);

        public Fixture(bool confidential = false, bool sessionless = false)
        {
            var agent = Entity("agent", "nova", new());
            Target = Entity("discussion", DiscussionStore.Slug("target"), new() {
                ["discussion_id"] = "target", ["app"] = "nova", ["status"] = "thinking", ["agent"] = agent.Id.ToString(),
                ["session_id"] = sessionless ? null : "target-session", ["owner_id"] = "test-owner",
                ["confidential"] = confidential, ["last_processed_session_assistant_uid"] = "",
            });
            live = Entity("discussion", DiscussionStore.Slug("live"), new() {
                ["discussion_id"] = "live", ["app"] = "nova", ["type"] = "live", ["status"] = "idle", ["agent"] = agent.Id.ToString(),
            });
            var all = new[] { Target, live, agent, Entity("plugin", "nova", new()) };
            var entities = Proxy.Create<IEntityStore>((method, args) => method switch {
                "GetBySlugAsync" => Task.FromResult(all.FirstOrDefault(e => e.Slug == (string)args[0]!)),
                "GetAsync" => Task.FromResult(all.FirstOrDefault(e => e.Id == (Guid)args[0]!)),
                "QueryAsync" => Task.FromResult<IReadOnlyList<LeafEntity>>(all.Where(e => e.TypeSlug == ((EntityQuery)args[0]!).TypeSlug).ToArray()),
                _ => throw new NotSupportedException(method),
            });
            async Task<IReadOnlyList<DiscussionMessage>> ReadPageAsync(object?[] args)
            {
                // Model an asynchronous database read so concurrent callbacks
                // actually overlap while the discussion gate is held.
                await Task.Yield();
                lock (Records)
                {
                    var after = (long)args[2]!;
                    PageReads.Add(after);
                    return Records.Where(m => m.DiscussionId == (Guid)args[0]! && m.Id > after)
                        .OrderBy(m => m.Id).Take((int)args[1]!).ToArray();
                }
            }
            var discussions = Proxy.Create<IDiscussions>((method, args) => {
                if (method == "GetMessagesAsync") return ReadPageAsync(args);
                if (method == "PostAsync")
                {
                    lock (Records) Records.Add(new(Records.Count + 1, (Guid)args[0]!, (string)args[1]!, (string)args[2]!, (JsonObject)args[3]!, DateTimeOffset.UtcNow));
                    return Task.CompletedTask;
                }
                throw new NotSupportedException(method);
            });
            var events = Proxy.Create<IPluginEvents>((method, _) => method == "Subscribe" ? new Noop() : Task.CompletedTask);
            agents = new AgentDirectory(entities, events) { NovaAgentId = agent.Id.ToString() };
            var client = new RedComputeClient(Gateway);
            Store = new DiscussionStore(entities, discussions);
            Injector = new EventInjector(discussions, events, client, agents, entities, new ConversationUnread(Store, discussions, client));
            liveEvents = new LiveEvents(Store, Injector, agents);
        }

        public async Task<int> Complete(string? id, string status = "Idle", string? stopReason = null,
            string? reason = null, string? deliveredMessageUid = null, string? promptMessageUid = null, bool executionObserved = false)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            ctx.Request.QueryString = new("?discussionId=target");
            ctx.Request.ContentType = "application/json";
            var body = new JsonObject { ["sessionId"] = "delegated-session", ["status"] = status, ["title"] = "same title", ["stopReason"] = stopReason };
            body["reason"] = reason;
            body["deliveredMessageUid"] = deliveredMessageUid;
            body["promptMessageUid"] = promptMessageUid;
            body["executionObserved"] = executionObserved;
            if (id is not null) body["callbackId"] = id;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
            var result = await CallbackEndpoints.HandleSessionCompleteAsync(ctx, Store, Injector, liveEvents);
            return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        }
        public void Dispose() => agents.Dispose();
    }

    private sealed class Gateway : IComputeGateway
    {
        public Dictionary<string, string> Admitted { get; } = [];
        public int Attempts { get; private set; }
        public bool FailNext { get; set; }
        public bool AcceptBeforeFailure { get; set; }
        public List<string?> Keys { get; } = [];
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Assert.EndsWith("/message", request.RequestUri!.ToString());
            Assert.NotNull(provenance);
            Attempts++;
            var key = request.Headers.TryGetValues("X-Idempotency-Key", out var values) ? values.Single() : null;
            Keys.Add(key);
            if (key is not null) Assert.InRange(key.Length, 1, 256);
            var body = await request.Content!.ReadAsStringAsync(ct);
            var fail = FailNext;
            FailNext = false;
            if (!fail || AcceptBeforeFailure)
            {
                var admission = key ?? Guid.NewGuid().ToString();
                if (Admitted.TryGetValue(admission, out var original)) Assert.Equal(original, body);
                else Admitted.Add(admission, body);
            }
            if (fail) throw new HttpRequestException("Simulated lost acknowledgement");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"accepted\":true}") };
        }
    }

    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args ?? []);
        public static T Create<T>(Func<string, object?[], object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, Proxy>();
            ((Proxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }
    private sealed class Noop : IDisposable { public void Dispose() { } }
    private static LeafEntity Entity(string type, string slug, JsonObject data) => new(Guid.NewGuid(), type, slug, slug, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
}
