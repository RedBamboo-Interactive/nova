using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionTitlePolicyTests
{
    [Fact]
    public async Task ExplicitCreateIsSystemOwned()
    {
        var fixture = new Fixture();
        var discussion = await fixture.Store.CreateAsync("Heartbeat title", null, "owner");

        Assert.Equal(DiscussionTitleSource.System, discussion.TitleSource);
        var json = JsonSerializer.Serialize(DiscussionStore.ToInfo(discussion));
        Assert.Contains("\"titleSource\":\"system\"", json);
    }


    [Fact]
    public async Task FallbackRefinesAndSessionMayRefineAgain()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add(title: null);

        var fallback = await fixture.Store.TrySetFallbackTitleAsync(
            discussion.EntityId, new string('a', 61));
        var refined = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Refined title");
        var refinedAgain = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Better refined title");

        Assert.Equal(new string('a', 59) + "…", fallback!.Title);
        Assert.Equal(DiscussionTitleSource.Fallback, fallback.TitleSource);
        Assert.Equal("Refined title", refined!.Title);
        Assert.Equal("Better refined title", refinedAgain!.Title);
        Assert.Equal(DiscussionTitleSource.Session, refinedAgain.TitleSource);
    }

    [Fact]
    public async Task RepeatedSessionTitleIsWriteIdempotent()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Refined", DiscussionTitleSource.Session);

        var before = fixture.PatchCalls;
        var result = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "  Refined  ");

        Assert.Equal("Refined", result!.Title);
        Assert.Equal(before, fixture.PatchCalls);
    }

    [Fact]
    public async Task FirstFallbackNeverOverwritesAnExistingTitle()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Existing title", DiscussionTitleSource.System);

        var result = await fixture.Store.TrySetFallbackTitleAsync(
            discussion.EntityId, "Late first message");

        Assert.Equal("Existing title", result!.Title);
        Assert.Equal(DiscussionTitleSource.System, result.TitleSource);
    }

    [Theory]
    [InlineData(DiscussionTitleSource.Manual)]
    [InlineData(DiscussionTitleSource.System)]
    [InlineData(DiscussionTitleSource.LegacyLocked)]
    public async Task ProtectedSourcesRejectSessionTitles(string source)
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Protected", source);

        var result = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Session title");

        Assert.Equal("Protected", result!.Title);
        Assert.Equal(source, result.TitleSource);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("heartbeat")]
    public async Task StandingDiscussionTypesRejectSessionTitles(string type)
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Standing title", DiscussionTitleSource.Fallback, type);

        var result = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Drifting session title");

        Assert.Equal("Standing title", result!.Title);
        Assert.Equal(DiscussionTitleSource.Fallback, result.TitleSource);
    }

    [Fact]
    public async Task ExactLegacyFallbackIsEligibleAndQueuedMessagesAreIgnored()
    {
        var fixture = new Fixture();
        const string visible = "First line\nsecond line";
        var discussion = fixture.Add(visible);
        fixture.Messages.Add(Message(discussion.EntityId, 1, "queued-user-message", "wrong"));
        fixture.Messages.Add(Message(discussion.EntityId, 2, "user-message",
            $"<nova-context>private</nova-context>{visible}"));

        var result = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Semantic title");

        Assert.Equal("Semantic title", result!.Title);
        Assert.Equal(DiscussionTitleSource.Session, result.TitleSource);
    }

    [Fact]
    public async Task ArbitraryLegacyTitleIsClassifiedLocked()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Hand-written legacy title");
        fixture.Messages.Add(Message(discussion.EntityId, 1, "user-message", "First message"));

        var result = await fixture.Store.TryApplySessionTitleAsync(
            discussion.EntityId, "Semantic title");

        Assert.Equal("Hand-written legacy title", result!.Title);
        Assert.Equal(DiscussionTitleSource.LegacyLocked, result.TitleSource);
    }

    [Fact]
    public async Task ManualRenameReturnsCanonicalManualSource()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add("Old", DiscussionTitleSource.Session);

        var result = await fixture.Store.SetManualTitleAsync(discussion.EntityId, "Mine");

        Assert.Equal("Mine", result!.Title);
        Assert.Equal(DiscussionTitleSource.Manual, result.TitleSource);
    }

    private static DiscussionMessage Message(Guid entityId, long id, string source, string content)
        => new(id, entityId, "user", content, new JsonObject { ["source"] = source },
            DateTimeOffset.UtcNow.AddSeconds(id));

    private sealed class Fixture
    {
        private readonly MemoryEntities entities = new();
        private readonly MemoryDiscussions discussions;

        public Fixture()
        {
            discussions = new MemoryDiscussions(entities);
            Store = new DiscussionStore(entities, discussions);
        }
        public int PatchCalls => entities.PatchCalls;

        public DiscussionStore Store { get; }
        public List<DiscussionMessage> Messages => discussions.Messages;

        public DiscussionRead Add(string? title, string? source = null, string type = "chat")
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var data = new JsonObject
            {
                ["discussion_id"] = id,
                ["app"] = "nova",
                ["type"] = type,
                ["status"] = "idle",
            };
            if (source is not null) data["title_source"] = source;
            var entity = NewEntity(id, title, data);
            entities.Items.Add(entity);
            return Store.GetAsync(id).GetAwaiter().GetResult()!;
        }

        private static LeafEntity NewEntity(string id, string? title, JsonObject data) => new(
            Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), title ?? $"Discussion {id}", data,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public int PatchCalls { get; private set; }
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafEntity>>(Items.Where(entity => entity.TypeSlug == query.TypeSlug).ToList());
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            var index = Items.FindIndex(entity => entity.Id == id);
            PatchCalls++;
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

    private sealed class MemoryDiscussions(MemoryEntities entities) : IDiscussions
    {
        public List<DiscussionMessage> Messages { get; } = [];
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default)
        {
            var copy = data?.DeepClone() as JsonObject ?? [];
            copy["agent"] = agentSlugOrId;
            copy["status"] ??= "idle";
            var id = copy["discussion_id"]!.GetValue<string>();
            var entity = new LeafEntity(Guid.NewGuid(), "discussion", DiscussionStore.Slug(id),
                title ?? $"Discussion {id}", copy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
            entities.Items.Add(entity);
            return Task.FromResult(entity);
        }
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>(Messages.Where(message => message.DiscussionId == discussionId && message.Id > afterId).OrderBy(message => message.Id).Take(limit).ToList());
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();
        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
