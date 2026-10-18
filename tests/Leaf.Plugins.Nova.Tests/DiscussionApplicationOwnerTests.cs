using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// A discussion stamped with another plugin's <c>owner_plugin</c> (Storyboard campaign
/// Agent threads) must open in Nova without Nova writing to it, and Nova lifecycle
/// changes must be refused explicitly instead of surfacing the kernel's ownership 500.
/// </summary>
[Collection(nameof(DiscussionApplicationOwnerTests))]
public sealed class DiscussionApplicationOwnerTests : IDisposable
{
    private static readonly Guid NovaPlugin = Guid.Parse("9c2c8fe1-d3e3-fd5c-86c3-793c5b97a977");
    private static readonly Guid StoryboardPlugin = Guid.Parse("e765a440-43d7-0e5a-b3b1-d1088afad217");

    public DiscussionApplicationOwnerTests() => DiscussionApplicationOwner.NovaPluginEntityId = NovaPlugin;

    public void Dispose() => DiscussionApplicationOwner.NovaPluginEntityId = null;

    [Fact]
    public async Task SessionHousekeepingSkipsForeignDiscussionWithoutWriting()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add(StoryboardPlugin, "storyboard");

        var status = await fixture.Store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking);
        var titled = await fixture.Store.TryApplySessionTitleAsync(discussion.EntityId, "Session title");
        var fallback = await fixture.Store.TrySetFallbackTitleAsync(discussion.EntityId, "First message");
        var reconciled = await fixture.Store.ReconcileConversationAsync(discussion.EntityId, "assistant-uid");
        var read = await fixture.Store.MarkConversationReadAsync(discussion.EntityId, 1);
        await fixture.Store.TouchAsync(discussion.EntityId);

        Assert.Equal(DiscussionStatus.Idle, status);
        Assert.Equal("New discussion", titled!.Title);
        Assert.Equal("New discussion", fallback!.Title);
        Assert.Null(reconciled!.LastProcessedSessionAssistantUid);
        Assert.Equal(0, read!.ReadConversationRevision);
        Assert.Equal(0, fixture.Entities.PatchCalls);
    }

    [Fact]
    public async Task LifecycleWritesOnForeignDiscussionAreRefused()
    {
        var fixture = new Fixture();
        var discussion = fixture.Add(StoryboardPlugin, "storyboard");
        var id = discussion.EntityId;

        // Archiving would go on to stop the owner's session.
        await AssertRefused(() => fixture.Store.TrySetStatusAsync(id, DiscussionStatus.Archiving));
        await AssertRefused(() => fixture.Store.SetManualTitleAsync(id, "Renamed"));
        await AssertRefused(() => fixture.Store.SetGeneratedTitleAsync(id, "Generated"));
        await AssertRefused(() => fixture.Store.ResetConversationReadAsync(id));
        await AssertRefused(() => fixture.Store.TryReopenArchivedAsync(id));
        await AssertRefused(() => fixture.Store.PatchAsync(id, new JsonObject { ["confidential"] = true }));
        await AssertRefused(() => fixture.Store.PostMessageAsync(id, "user", "hello", []));
        await AssertRefused(() => fixture.Store.PostMessageIdempotentAsync(id, "user", "hello", [], "key"));
        await AssertRefused(() => fixture.Store.PostConversationMessageAsync(id, "hello", [], "key"));

        Assert.Equal(0, fixture.Entities.PatchCalls);
        Assert.Empty(fixture.Discussions.Posted);
    }

    [Fact]
    public async Task NovaOwnedDiscussionsKeepTheirHousekeeping()
    {
        var fixture = new Fixture();
        var unstamped = fixture.Add(null, null);
        var stampedByNova = fixture.Add(NovaPlugin, null);

        foreach (var discussion in new[] { unstamped, stampedByNova })
        {
            var status = await fixture.Store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking);
            await fixture.Store.ReconcileConversationAsync(discussion.EntityId, null);
            var advanced = await fixture.Store.ReconcileConversationAsync(discussion.EntityId, "assistant-uid");
            var read = await fixture.Store.MarkConversationReadAsync(discussion.EntityId, 1);
            var renamed = await fixture.Store.SetManualTitleAsync(discussion.EntityId, "Renamed");

            Assert.Equal(DiscussionStatus.Thinking, status);
            Assert.Equal(1, advanced!.ConversationRevision);
            Assert.Equal(1, read!.ReadConversationRevision);
            Assert.Equal("Renamed", renamed!.Title);
            Assert.False(DiscussionApplicationOwner.IsForeign(renamed));
        }
    }

    [Fact]
    public async Task ProjectionNamesTheOwningApplicationOnlyForForeignDiscussions()
    {
        var fixture = new Fixture();
        var foreign = fixture.Add(StoryboardPlugin, "storyboard");
        var nova = fixture.Add(null, null);

        var foreignJson = JsonSerializer.Serialize(DiscussionStore.ToInfo(foreign));
        var novaJson = JsonSerializer.Serialize(DiscussionStore.ToInfo(nova));

        Assert.Contains("\"ownedBy\":{\"app\":\"storyboard\"}", foreignJson);
        Assert.Contains("\"ownedBy\":null", novaJson);
        await Task.CompletedTask;
    }

    [Fact]
    public void RefusalIsAConflictNamingTheOwner()
    {
        var discussion = new Fixture().Add(StoryboardPlugin, "storyboard");

        var exception = new DiscussionOwnedByApplicationException(discussion);

        Assert.Same(discussion, exception.Discussion);
        Assert.Equal(
            "This discussion belongs to storyboard and is read-only in Nova.",
            exception.Message);
        Assert.Equal("owned_by_application", DiscussionApplicationOwner.ErrorCode);
    }

    private static async Task AssertRefused(Func<Task> write)
        => await Assert.ThrowsAsync<DiscussionOwnedByApplicationException>(write);

    private sealed class Fixture
    {
        public MemoryEntities Entities { get; } = new();
        public MemoryDiscussions Discussions { get; }
        public DiscussionStore Store { get; }

        public Fixture()
        {
            Discussions = new MemoryDiscussions();
            Store = new DiscussionStore(Entities, Discussions);
        }

        public DiscussionRead Add(Guid? ownerPlugin, string? sourceApp)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var data = new JsonObject
            {
                ["discussion_id"] = id,
                ["app"] = "nova",
                ["type"] = "chat",
                ["status"] = "idle",
                ["title"] = "New discussion",
                ["title_source"] = DiscussionTitleSource.Fallback,
            };
            if (ownerPlugin is not null) data["owner_plugin"] = ownerPlugin.Value.ToString();
            if (sourceApp is not null) data["source_app"] = sourceApp;
            Entities.Items.Add(new LeafEntity(
                Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), "New discussion", data,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
            return Store.GetAsync(id).GetAwaiter().GetResult()!;
        }
    }

    /// <summary>Enforces plugin ownership the way the kernel's plugin-scoped store does.</summary>
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
            var entity = Items[index];
            if (Guid.TryParse(entity.Data["owner_plugin"]?.GetValue<string>(), out var owner) && owner != NovaPlugin)
                throw new InvalidOperationException(
                    $"Plugin 'nova' cannot modify entity '{entity.Slug}' -- it is owned by another plugin");
            PatchCalls++;
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
        public List<string> Posted { get; } = [];
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default)
        {
            Posted.Add(content);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();
        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
