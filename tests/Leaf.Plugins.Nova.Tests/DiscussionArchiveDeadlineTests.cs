using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Production had discussions stuck in <c>archiving</c> ("Finalizing…") for months: the
/// finalizer only completes once RedCompute reports the session gone, and its stop call
/// (a POST made from the background sweep, which has no execution identity) never lands.
/// The wait is now bounded; these pin the deadline and everything it must not change.
/// </summary>
public sealed class DiscussionArchiveDeadlineTests
{
    private static readonly TimeSpan Deadline = DiscussionLifecycle.ForceFinalizeAfter;

    [Fact]
    public async Task Normal_archive_finalizes_promptly_when_the_stop_lands()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: true);

        await fixture.Lifecycle.BeginArchiveAsync(chat);
        await fixture.Lifecycle.FinalizeAsync(chat.EntityId, "s1");

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
        Assert.True(fixture.Compute.StopCalls("s1") >= 1);
    }

    [Fact]
    public async Task Session_that_ignores_the_stop_stays_archiving_inside_the_deadline()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);
        await fixture.BeginAndSettleAsync(chat);

        // Well past the client's 5 s undo window, and just short of the deadline.
        foreach (var elapsed in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), Deadline - TimeSpan.FromSeconds(1) })
        {
            fixture.Clock.Set(elapsed);
            await fixture.Lifecycle.ReconcilePendingAsync();
            Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));
        }
    }

    [Fact]
    public async Task Still_idle_session_is_finalized_once_the_deadline_passes_after_one_more_stop()
    {
        var fixture = new Fixture();
        // Found already archiving, so no fire-and-forget first attempt races the count.
        var chat = fixture.Add("chat", "s1", status: DiscussionStatus.Archiving);
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);
        await fixture.Lifecycle.ReconcilePendingAsync();
        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));
        var stopsBefore = fixture.Compute.StopCalls("s1");

        fixture.Clock.Set(Deadline);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
        Assert.True(fixture.Compute.StopCalls("s1") > stopsBefore, "the stop is re-issued on the finalizing pass");
        Assert.Contains(fixture.Logs, l => l.Contains("without a confirmed stop") && l.Contains("s1"));
    }

    [Fact]
    public async Task Stop_that_throws_does_not_prevent_the_deadline_finalize()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: false, stopThrows: true);
        await fixture.BeginAndSettleAsync(chat);
        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));

        fixture.Clock.Set(Deadline);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
    }

    [Fact]
    public async Task Missing_session_finalizes_immediately()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "gone");

        await fixture.Lifecycle.BeginArchiveAsync(chat);
        await fixture.Lifecycle.FinalizeAsync(chat.EntityId, "gone");

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
    }

    [Fact]
    public async Task Unreachable_RedCompute_never_forces_a_finalize_even_past_the_deadline()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);
        fixture.Compute.Unreachable = true;
        await fixture.BeginAndSettleAsync(chat);

        fixture.Clock.Set(Deadline * 3);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));

        // Once RedCompute answers, an overdue discussion with a live session is finalized.
        fixture.Compute.Unreachable = false;
        await fixture.Lifecycle.ReconcilePendingAsync();
        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
    }

    [Fact]
    public async Task Discussion_found_archiving_after_a_restart_gets_a_fresh_bounded_wait()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1", status: DiscussionStatus.Archiving);
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);

        await fixture.Lifecycle.ReconcilePendingAsync();
        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));

        fixture.Clock.Set(Deadline - TimeSpan.FromSeconds(1));
        await fixture.Lifecycle.ReconcilePendingAsync();
        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));

        fixture.Clock.Set(Deadline);
        await fixture.Lifecycle.ReconcilePendingAsync();
        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
    }

    [Fact]
    public async Task Live_rotation_archives_the_old_live_and_leaves_the_replacement_alone()
    {
        var fixture = new Fixture();
        var oldLive = fixture.Add("live", "live-old");
        var newLive = fixture.Add("live", "live-new", status: DiscussionStatus.Idle);
        fixture.Compute.SetSession("live-old", "Idle", stopLands: true);
        fixture.Compute.SetSession("live-new", "Idle", stopLands: false);

        Assert.Equal(DiscussionStatus.Archiving, await fixture.Lifecycle.BeginArchiveAsync(oldLive));
        await fixture.Lifecycle.FinalizeAsync(oldLive.EntityId, "live-old");
        fixture.Clock.Set(Deadline * 2);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(oldLive));
        Assert.Equal(DiscussionStatus.Idle, fixture.StatusOf(newLive));
        Assert.Equal(0, fixture.Compute.StopCalls("live-new"));
    }

    [Fact]
    public async Task Live_rotation_whose_old_session_will_not_stop_still_finalizes_at_the_deadline()
    {
        var fixture = new Fixture();
        var oldLive = fixture.Add("live", "live-old");
        fixture.Compute.SetSession("live-old", "Idle", stopLands: false);
        await fixture.BeginAndSettleAsync(oldLive);

        fixture.Clock.Set(Deadline);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(oldLive));
    }

    [Fact]
    public async Task Archived_is_still_terminal_for_the_ordinary_status_writer()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);
        await fixture.BeginAndSettleAsync(chat);
        fixture.Clock.Set(Deadline);
        await fixture.Lifecycle.ReconcilePendingAsync();

        Assert.Null(await fixture.Store.TrySetStatusAsync(chat.EntityId, DiscussionStatus.Idle));
        Assert.Equal(DiscussionStatus.Archived, fixture.StatusOf(chat));
    }

    [Fact]
    public async Task Archiving_remains_closed_to_the_ordinary_status_writer_until_finalized()
    {
        var fixture = new Fixture();
        var chat = fixture.Add("chat", "s1");
        fixture.Compute.SetSession("s1", "Idle", stopLands: false);
        await fixture.BeginAndSettleAsync(chat);

        Assert.Null(await fixture.Store.TrySetStatusAsync(chat.EntityId, DiscussionStatus.Idle));
        Assert.Equal(DiscussionStatus.Archiving, fixture.StatusOf(chat));
    }

    private sealed class Fixture
    {
        public ManualClock Clock { get; } = new();
        public FakeCompute Compute { get; } = new();
        public List<string> Logs { get; } = [];
        public MemoryEntities Entities { get; } = new();
        public DiscussionStore Store { get; }
        public DiscussionLifecycle Lifecycle { get; }

        public Fixture()
        {
            Store = new DiscussionStore(Entities, new MemoryDiscussions());
            Lifecycle = new DiscussionLifecycle(Store, new RedComputeClient(Compute),
                new ListLogger(Logs), Clock);
        }

        public DiscussionRead Add(string type, string sessionId, string status = DiscussionStatus.Idle)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var data = new JsonObject
            {
                ["discussion_id"] = id,
                ["app"] = "nova",
                ["type"] = type,
                ["status"] = status,
                ["session_id"] = sessionId,
                ["title"] = id,
            };
            Entities.Items.Add(new LeafEntity(Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), id, data,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
            return Store.GetAsync(id).GetAwaiter().GetResult()!;
        }

        /// <summary>Commit the intent, then let the fire-and-forget first attempt finish
        /// so the test controls every later attempt.</summary>
        public async Task BeginAndSettleAsync(DiscussionRead discussion)
        {
            await Lifecycle.BeginArchiveAsync(discussion);
            await Lifecycle.FinalizeAsync(discussion.EntityId, discussion.SessionId);
        }

        public string StatusOf(DiscussionRead discussion)
            => Entities.Items.Single(e => e.Id == discussion.EntityId).Data["status"]!.GetValue<string>();
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly DateTimeOffset start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        private TimeSpan elapsed;
        public void Set(TimeSpan sinceStart) => elapsed = sinceStart;
        public override DateTimeOffset GetUtcNow() => start + elapsed;
    }

    private sealed class FakeCompute : IComputeGateway
    {
        private readonly Dictionary<string, (string Status, bool StopLands, bool StopThrows)> sessions = [];
        private readonly Dictionary<string, int> stops = [];
        public bool Unreachable { get; set; }

        public void SetSession(string id, string status, bool stopLands, bool stopThrows = false)
            => sessions[id] = (status, stopLands, stopThrows);

        public int StopCalls(string id) => stops.GetValueOrDefault(id);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = parts.Length >= 3 ? parts[2] : "";
            var isStop = request.Method == HttpMethod.Post && path.EndsWith("/stop", StringComparison.Ordinal);

            if (isStop)
            {
                stops[id] = stops.GetValueOrDefault(id) + 1;
                // The production failure: no execution identity on a background POST.
                if (sessions.TryGetValue(id, out var s) && s.StopThrows)
                    throw new InvalidOperationException("RedCompute mutations require a signed execution identity");
                if (Unreachable) throw new HttpRequestException("down");
                if (sessions.TryGetValue(id, out s) && s.StopLands)
                    sessions[id] = s with { Status = "Stopped" };
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }

            if (Unreachable) throw new HttpRequestException("down");
            if (!sessions.TryGetValue(id, out var session))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var payload = JsonSerializer.Serialize(new { session = new { id, status = session.Status } });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ListLogger(List<string> sink) : ILogger<DiscussionLifecycle>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            IEnumerable<LeafEntity> rows = Items.Where(entity => entity.TypeSlug == query.TypeSlug)
                .Where(e => query.DataEquals == null || query.DataEquals.All(pair =>
                    e.Data[pair.Key]?.ToJsonString() == JsonSerializer.Serialize(pair.Value)));
            if (query.AfterId is { } after) rows = rows.Where(e => e.Id.CompareTo(after) > 0);
            if (query.OrderById) rows = rows.OrderBy(e => e.Id);
            return Task.FromResult<IReadOnlyList<LeafEntity>>(rows.Skip(query.Offset)
                .Take(Math.Clamp(query.Limit, 1, 500)).ToList());
        }
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            var index = Items.FindIndex(entity => entity.Id == id);
            var entity = Items[index];
            foreach (var (key, value) in patch) entity.Data[key] = value?.DeepClone();
            var updated = entity with { Name = name ?? entity.Name, UpdatedAt = DateTimeOffset.UtcNow };
            Items[index] = updated;
            return Task.FromResult(updated);
        }
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MemoryDiscussions : IDiscussions
    {
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
