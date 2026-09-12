using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionTitleTests
{
    [Fact]
    public async Task ManualTitleSurvivesLaterSessionSuggestion()
    {
        var entity = Discussion("Initial title");
        var entities = new InMemoryEntityStore(entity);
        var store = new DiscussionStore(entities, new UnusedDiscussions());

        var renamed = await store.SetManualTitleAsync(entity.Id, "My chosen title");
        var afterSuggestion = await store.TrySetSuggestedTitleAsync(entity.Id, "Older session title");

        Assert.Equal("My chosen title", renamed!.Title);
        Assert.Equal("My chosen title", afterSuggestion!.Title);
        Assert.True(entities.Current.Data["title_is_manual"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SuggestedTitlesStillRefreshBeforeAUserRename()
    {
        var entity = Discussion("First message title", titleIsManual: false);
        var entities = new InMemoryEntityStore(entity);
        var store = new DiscussionStore(entities, new UnusedDiscussions());

        var updated = await store.TrySetSuggestedTitleAsync(entity.Id, "Generated session title");

        Assert.Equal("Generated session title", updated!.Title);
        Assert.False(entities.Current.Data["title_is_manual"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LegacyNamedTitleIsPreservedAndMigratedToManual()
    {
        var entity = Discussion("My title from before the upgrade");
        var entities = new InMemoryEntityStore(entity);
        var store = new DiscussionStore(entities, new UnusedDiscussions());

        var updated = await store.TrySetSuggestedTitleAsync(entity.Id, "Later session title");

        Assert.Equal("My title from before the upgrade", updated!.Title);
        Assert.Equal("My title from before the upgrade", entities.Current.Name);
        Assert.True(entities.Current.Data["title_is_manual"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LegacyUntitledDiscussionStillAcceptsASuggestion()
    {
        var entity = Discussion(null);
        var entities = new InMemoryEntityStore(entity);
        var store = new DiscussionStore(entities, new UnusedDiscussions());

        var updated = await store.TrySetSuggestedTitleAsync(entity.Id, "Generated session title");

        Assert.Equal("Generated session title", updated!.Title);
        Assert.False(entities.Current.Data["title_is_manual"]!.GetValue<bool>());
    }

    private static LeafEntity Discussion(string? title, bool? titleIsManual = null)
    {
        const string id = "title-test";
        var data = new JsonObject
        {
            ["discussion_id"] = id,
            ["app"] = "nova",
            ["title"] = title,
            ["status"] = DiscussionStatus.Idle,
            ["type"] = "chat",
        };
        if (titleIsManual is not null)
            data["title_is_manual"] = titleIsManual.Value;

        return new LeafEntity(
            Guid.NewGuid(),
            "discussion",
            DiscussionStore.Slug(id),
            title ?? $"Discussion {id}",
            data,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "test");
    }

    private sealed class InMemoryEntityStore(LeafEntity entity) : IEntityStore
    {
        public LeafEntity Current { get; private set; } = entity;

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<LeafEntity?>(Current.Id == id ? Current : null);

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult<LeafEntity?>(Current.Slug == slug ? Current : null);

        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default)
            => Task.FromResult<LeafEntity?>(Current.TypeSlug == typeSlug && Current.Slug == slug ? Current : null);

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafEntity>>([]);

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            foreach (var (key, value) in patch)
                Current.Data[key] = value?.DeepClone();
            Current = Current with { Name = name ?? Current.Name, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(Current);
        }

        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedDiscussions : IDiscussions
    {
        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);

        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);

        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafRecord>>([]);

        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage)
            => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
