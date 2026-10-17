using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionListingTests
{
    [Fact]
    public async Task Open_listing_is_complete_when_newer_archived_rows_fill_the_first_page()
    {
        var now = DateTimeOffset.UtcNow;
        var entities = Enumerable.Range(0, 513)
            .Select(i => Discussion($"archived-{i}", DiscussionStatus.Archived,
                now.AddMinutes(i), "agent-a"))
            .Concat(new[]
            {
                Discussion("open-newest", DiscussionStatus.Thinking, now.AddDays(-1).AddMinutes(2), "agent-a"),
                Discussion("open-middle", DiscussionStatus.Idle, now.AddDays(-1).AddMinutes(1), null),
                Discussion("open-oldest", DiscussionStatus.Stopped, now.AddDays(-1), "agent-a"),
                Discussion("other-agent", DiscussionStatus.Idle, now.AddDays(-1).AddMinutes(3), "agent-b"),
            })
            .ToArray();
        var entityStore = new ContractEntityStore(entities);
        var store = new DiscussionStore(entityStore, new NoIoDiscussions());

        var result = await store.ListOpenAsync("agent-a");

        Assert.Equal(["open-newest", "open-middle", "open-oldest"],
            result.Select(discussion => discussion.Id).ToArray());
        Assert.True(entityStore.QueryCount > 1);
    }

    [Fact]
    public async Task Archived_listing_pages_past_the_sdk_row_limit()
    {
        var now = DateTimeOffset.UtcNow;
        var entityStore = new ContractEntityStore(Enumerable.Range(0, 513)
            .Select(i => Discussion($"archived-{i}", DiscussionStatus.Archived,
                now.AddMinutes(i), "agent-a"))
            .Append(Discussion("open", DiscussionStatus.Idle, now.AddDays(1), "agent-a"))
            .ToArray());
        var store = new DiscussionStore(entityStore, new NoIoDiscussions());

        var result = await store.ListByStatusAsync(DiscussionStatus.Archived);

        Assert.Equal(513, result.Count);
        Assert.All(result, discussion => Assert.Equal(DiscussionStatus.Archived, discussion.Status));
        Assert.Equal("archived-512", result[0].Id);
        Assert.True(entityStore.QueryCount > 1);
    }

    [Fact]
    public async Task Open_listing_never_requests_closed_statuses()
    {
        var entityStore = new ContractEntityStore(
            Discussion("open", DiscussionStatus.Idle, DateTimeOffset.UtcNow, "agent-a"),
            Discussion("archived", DiscussionStatus.Archived, DateTimeOffset.UtcNow, "agent-a"));
        var store = new DiscussionStore(entityStore, new NoIoDiscussions());

        var result = await store.ListOpenAsync();

        Assert.Equal("open", Assert.Single(result).Id);
        Assert.Equal(
            [DiscussionStatus.Idle, DiscussionStatus.Thinking, DiscussionStatus.Stopped],
            entityStore.Queries.Select(query =>
                Assert.IsType<string>(query.DataEquals!["status"])).ToArray());
        Assert.All(entityStore.Queries, query =>
        {
            Assert.Equal("nova", query.DataEquals!["app"]);
            Assert.False(query.DataEquals.ContainsKey("type"));
        });
    }

    [Fact]
    public async Task Type_scoped_lookup_cannot_miss_a_discussion_that_races_between_open_statuses()
    {
        var row = Discussion("live", DiscussionStatus.Idle, DateTimeOffset.UtcNow,
            "agent-a", type: "live");
        var entityStore = new ContractEntityStore(row);
        var store = new DiscussionStore(entityStore, new NoIoDiscussions());
        entityStore.BeforeQuery = query =>
        {
            if (query.DataEquals?.TryGetValue("status", out var status) != true) return;
            row.Data["status"] = Equals(status, DiscussionStatus.Idle)
                ? DiscussionStatus.Thinking
                : DiscussionStatus.Idle;
        };

        Assert.Empty(await store.ListOpenAsync());

        row.Data["status"] = DiscussionStatus.Idle;
        var result = await store.ListByTypeAsync("live", "agent-a");

        Assert.Equal("live", Assert.Single(result).Id);
        var typeQuery = entityStore.Queries[^1];
        Assert.Equal("live", typeQuery.DataEquals!["type"]);
        Assert.False(typeQuery.DataEquals.ContainsKey("status"));
    }

    private static LeafEntity Discussion(
        string id, string status, DateTimeOffset lastActivity, string? agentId,
        string type = "chat")
        => new(
            Guid.NewGuid(),
            "discussion",
            DiscussionStore.Slug(id),
            id,
            new JsonObject
            {
                ["discussion_id"] = id,
                ["app"] = "nova",
                ["status"] = status,
                ["agent"] = agentId,
                ["type"] = type,
                ["last_activity"] = lastActivity.ToString("O"),
            },
            lastActivity,
            lastActivity,
            "test");

    private sealed class ContractEntityStore(params LeafEntity[] initial) : IEntityStore
    {
        private readonly List<LeafEntity> entities = [.. initial];

        public int QueryCount { get; private set; }
        public List<EntityQuery> Queries { get; } = [];
        public Action<EntityQuery>? BeforeQuery { get; set; }

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(entity => entity.Id == id));

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(entity => entity.Slug == slug));

        public Task<LeafEntity?> GetBySlugAsync(
            string typeSlug, string slug, CancellationToken ct = default)
            => Task.FromResult(entities.SingleOrDefault(entity =>
                entity.TypeSlug == typeSlug && entity.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(
            EntityQuery query, CancellationToken ct = default)
        {
            QueryCount++;
            Queries.Add(query);
            BeforeQuery?.Invoke(query);
            IEnumerable<LeafEntity> matches = entities
                .Where(entity => entity.TypeSlug == query.TypeSlug)
                .Where(entity => Matches(entity.Data, query.DataEquals));
            if (query.AfterId is { } afterId)
                matches = matches.Where(entity => entity.Id.CompareTo(afterId) > 0);
            matches = query.OrderById
                ? matches.OrderBy(entity => entity.Id)
                : matches.OrderByDescending(entity => entity.UpdatedAt).ThenBy(entity => entity.Id);
            return Task.FromResult<IReadOnlyList<LeafEntity>>(matches
                .Skip(query.Offset)
                .Take(Math.Clamp(query.Limit, 1, 500))
                .ToList());
        }

        public Task<LeafEntity> CreateAsync(
            string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<LeafEntity> UpsertBySlugAsync(
            string typeSlug, string slug, string name, JsonObject? data = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<LeafEntity> PatchAsync(
            Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<LeafEntity> ReplaceDataAsync(
            Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        private static bool Matches(
            JsonObject data, IReadOnlyDictionary<string, object?>? expected)
            => expected is null || expected.All(pair =>
                data[pair.Key]?.ToJsonString() == JsonSerializer.Serialize(pair.Value));
    }

    private sealed class NoIoDiscussions : IDiscussions
    {
        public Task<LeafEntity> CreateAsync(
            string? title, string? agentSlugOrId = null, JsonObject? data = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task PostAsync(
            Guid discussionId, string role, string content, JsonObject? metadata = null,
            string? userId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(
            Guid discussionId, int limit = 1000, long afterId = 0,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(
            string query, int limit = 1000, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task SetReactionAsync(
            Guid discussionId, ReactionChange change, CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(
            Guid discussionId, DateTimeOffset? since = null, int limit = 1000,
            CancellationToken ct = default)
            => throw new InvalidOperationException("This test store is read-only.");

        public IDisposable Subscribe(
            Guid discussionId, Func<DiscussionMessage, Task> onMessage)
            => throw new InvalidOperationException("This test store is read-only.");
    }
}
