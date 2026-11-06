using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// <c>local-user</c>, the loopback identity, stands for the installation owner: on a single-user
/// install the local browser keeps the owner's discussions, on a multi-user install it never reaches
/// another user's. Unowned and local discussions belong to that owner too, so a second signed user
/// neither reads nor claims them. Reading one discussion, listing, searching and exporting all decide through
/// <see cref="DiscussionAccessPolicy.CanRead"/>, so these cases hold for each of them.
/// </summary>
public sealed class LocalUserOwnerScopeTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string Owner = OwnerId.ToString();
    private static readonly string Second = SecondId.ToString();

    [Fact]
    public async Task The_installation_owner_is_the_recorded_setup_owner_else_the_first_account()
    {
        var entities = new MemoryEntities();
        Assert.Null(await new InstallationOwner(entities).ResolveAsync());

        entities.User(SecondId, createdAt: DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        entities.User(OwnerId, createdAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        Assert.Equal(Owner, await new InstallationOwner(entities).ResolveAsync());

        entities.SetupOwner(Second);
        Assert.Equal(Second, await new InstallationOwner(entities).ResolveAsync());
    }

    [Fact]
    public async Task Every_request_is_stamped_with_the_installation_owner()
    {
        var owner = new InstallationOwner(SingleUser().Entities);

        var local = LocalDefault();
        await owner.StampAsync(local);
        Assert.Equal(Owner, InstallationOwner.ForRequest(local));

        var signed = Human(Second);
        await owner.StampAsync(signed);
        Assert.Equal(Owner, InstallationOwner.ForRequest(signed));
    }

    [Fact]
    public async Task The_owner_is_cached_for_thirty_seconds()
    {
        var entities = new MemoryEntities();
        var clock = new ManualClock();
        var owner = new InstallationOwner(entities, clock);
        Assert.Null(await owner.ResolveAsync());

        entities.User(OwnerId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await owner.ResolveAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Owner, await owner.ResolveAsync());
    }

    [Theory]
    [InlineData("local-user")]
    [InlineData(null)]
    public async Task Multi_user_install_an_unowned_or_local_discussion_belongs_to_the_installation_owner(string? rowOwner)
    {
        var f = MultiUser();
        f.Discussion("shr00001", rowOwner);

        // A second signed user, and an Agent acting for them, are refused it.
        var second = await f.Stamped(Human(Second));
        Assert.False(await f.CanRead("shr00001", second));
        Assert.Empty(await f.Listed(second));
        Assert.False(await f.CanRead("shr00001", await f.Stamped(AgentExecution(Second))));

        // The installation owner signed in, an Agent acting for them, and local-user reach it.
        var owner = await f.Stamped(Human(Owner));
        Assert.True(await f.CanRead("shr00001", owner));
        Assert.Equal(["shr00001"], await f.Listed(owner));
        Assert.True(await f.CanRead("shr00001", await f.Stamped(AgentExecution(Owner))));
        Assert.True(await f.CanRead("shr00001", await f.Stamped(LocalDefault())));
    }

    [Fact]
    public async Task Multi_user_install_a_second_user_cannot_claim_a_local_discussion()
    {
        var f = MultiUser();
        f.Discussion("loc00001", "local-user");
        var second = await f.Stamped(Human(Second));
        var owner = await f.Stamped(Human(Owner));

        Assert.Equal("local-user", DiscussionOwnership.ResolveForSessionStart(
            "local-user", Second, needsSession: true, InstallationOwner.ForRequest(second)));
        Assert.Equal(Owner, DiscussionOwnership.ResolveForSessionStart(
            "local-user", Owner, needsSession: true, InstallationOwner.ForRequest(owner)));
    }

    [Fact]
    public async Task An_automation_discussion_created_before_sign_in_stays_the_owners()
    {
        // Before any account exists, an anonymous local automation creates its delivery discussion.
        var entities = new MemoryEntities();
        var clock = new ManualClock();
        var f = new Fixture(entities, clock);
        f.Discussion("dlv00001", "local-user");
        Assert.True(await f.CanRead("dlv00001", await f.Stamped(LocalDefault())));

        // Setup creates the owner, records them, and a second account is added later.
        entities.User(OwnerId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        entities.User(SecondId, DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        entities.SetupOwner(Owner);
        clock.Advance(TimeSpan.FromMinutes(1));

        var owner = await f.Stamped(Human(Owner));
        Assert.True(await f.CanRead("dlv00001", owner));
        Assert.Equal(Owner, DiscussionOwnership.ResolveForSessionStart(
            "local-user", Owner, needsSession: true, InstallationOwner.ForRequest(owner)));
        Assert.True(await f.CanRead("dlv00001", await f.Stamped(LocalDefault())));

        var second = await f.Stamped(Human(Second));
        Assert.False(await f.CanRead("dlv00001", second));
        Assert.Equal("local-user", DiscussionOwnership.ResolveForSessionStart(
            "local-user", Second, needsSession: true, InstallationOwner.ForRequest(second)));
    }

    [Theory]
    [InlineData("local-user")]
    [InlineData(null)]
    public async Task With_no_known_owner_unowned_and_local_discussions_stay_open(string? rowOwner)
    {
        var f = new Fixture(new MemoryEntities());
        f.Discussion("shr00001", rowOwner);

        Assert.True(await f.CanRead("shr00001", await f.Stamped(Human(Second))));
        Assert.True(await f.CanRead("shr00001", Human(Second))); // unstamped: the same old rule
        Assert.Equal("local-user", DiscussionOwnership.ResolveForSessionStart(
            "local-user", "local-user", needsSession: true, null));
        Assert.Equal(Second, DiscussionOwnership.ResolveForSessionStart(
            "local-user", Second, needsSession: true, null));
    }

    [Fact]
    public async Task Confidential_unowned_discussions_stay_closed()
    {
        var f = MultiUser();
        f.Discussion("con00002", null, confidential: true);

        Assert.False(await f.CanRead("con00002", await f.Stamped(Human(Owner))));
        Assert.False(await f.CanRead("con00002", await f.Stamped(LocalDefault())));
    }

    [Fact]
    public async Task Single_user_install_local_user_keeps_the_owners_discussions()
    {
        var f = SingleUser();
        f.Discussion("own00001", Owner);
        f.Discussion("loc00001", "local-user");
        f.Discussion("nul00001", null);
        var local = await f.Stamped(LocalDefault());

        Assert.True(await f.CanRead("own00001", local));
        Assert.Equal(["loc00001", "nul00001", "own00001"], await f.Listed(local));
    }

    [Fact]
    public async Task Single_user_install_a_bearer_whose_subject_is_local_user_keeps_the_owners_discussions()
    {
        var f = SingleUser();
        f.Discussion("own00001", Owner);

        Assert.True(await f.CanRead("own00001", await f.Stamped(Human("local-user"))));
    }

    [Fact]
    public async Task Multi_user_install_local_user_never_reaches_a_second_users_discussion()
    {
        var f = MultiUser();
        f.Discussion("own00001", Owner);
        f.Discussion("sec00001", Second);
        var local = await f.Stamped(LocalDefault());

        Assert.True(await f.CanRead("own00001", local));
        Assert.False(await f.CanRead("sec00001", local));
        Assert.Equal(["own00001"], await f.Listed(local));
    }

    [Fact]
    public async Task Local_user_with_no_known_owner_reaches_only_unowned_and_local_discussions()
    {
        var f = new Fixture(new MemoryEntities());
        f.Discussion("sec00001", Second);
        f.Discussion("loc00001", "local-user");
        f.Discussion("nul00001", null);

        // Stamped with no owner, and never stamped at all: both fail closed.
        Assert.Equal(["loc00001", "nul00001"], await f.Listed(await f.Stamped(LocalDefault())));
        Assert.Equal(["loc00001", "nul00001"], await f.Listed(LocalDefault()));
    }

    [Fact]
    public async Task A_signed_user_still_reads_only_their_own()
    {
        var f = MultiUser();
        f.Discussion("own00001", Owner);
        f.Discussion("sec00001", Second);

        var second = await f.Stamped(Human(Second));
        Assert.False(await f.CanRead("own00001", second));
        Assert.Equal(["sec00001"], await f.Listed(second));

        var owner = await f.Stamped(Human(Owner));
        Assert.False(await f.CanRead("sec00001", owner));
        Assert.Equal(["own00001"], await f.Listed(owner));
    }

    [Fact]
    public async Task Confidential_rules_are_unchanged_for_local_user()
    {
        var f = SingleUser();
        f.Discussion("con00001", Owner, confidential: true);

        Assert.False(await f.CanRead("con00001", await f.Stamped(LocalDefault())));
        Assert.True(await f.CanRead("con00001", await f.Stamped(Human(Owner))));
        Assert.False(await f.CanRead("con00001", await f.Stamped(Human(Second))));
    }

    [Theory]
    [InlineData(null, "local-user", null, true)]
    [InlineData("local-user", "someone", null, true)]
    [InlineData("user-a", "user-a", null, true)]
    [InlineData("user-a", "user-b", null, false)]
    [InlineData("user-a", "local-user", null, false)]
    [InlineData("user-a", "local-user", "user-a", true)]
    [InlineData("USER-A", "local-user", "user-a", true)]
    [InlineData("user-b", "local-user", "user-a", false)]
    [InlineData("user-a", "user-b", "user-a", false)]
    [InlineData(null, "user-b", "user-a", false)]
    [InlineData("local-user", "user-b", "user-a", false)]
    [InlineData(null, "user-a", "user-a", true)]
    [InlineData("local-user", "USER-A", "user-a", true)]
    [InlineData(null, "local-user", "user-a", true)]
    [InlineData(null, "user-b", null, true)]
    [InlineData(null, "system", "user-a", true)]
    [InlineData("local-user", "service:nova", "user-a", true)]
    [InlineData("system", "user-a", "user-a", false)]
    public void Owner_scope(string? ownerId, string? userId, string? installationOwnerId, bool expected)
        => Assert.Equal(expected, OwnerScope.CanAccess(ownerId, userId, installationOwnerId));

    private static Fixture SingleUser()
    {
        var entities = new MemoryEntities();
        entities.User(OwnerId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        return new Fixture(entities);
    }

    private static Fixture MultiUser()
    {
        var entities = new MemoryEntities();
        entities.User(OwnerId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        entities.User(SecondId, DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        entities.SetupOwner(Owner);
        return new Fixture(entities);
    }

    private static DefaultHttpContext Human(string userId)
        => Context(new ClaimsIdentity([new Claim("sub", userId)], "Bearer"));

    private static DefaultHttpContext LocalDefault()
        => Context(new ClaimsIdentity([new Claim("sub", "local-user")], "LocalDefault"));

    private static DefaultHttpContext AgentExecution(string beneficiaryId)
        => Context(new ClaimsIdentity([
            new Claim("sub", beneficiaryId),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", System.Text.Json.JsonSerializer.Serialize(new
            {
                app = new { id = "nova", name = "Nova" },
                actor = new { kind = "agent", id = "nova", entityId = "agent-nova" },
                beneficiary = new { kind = "user", id = beneficiaryId },
            })),
        ], "Bearer"));

    private static DefaultHttpContext Context(ClaimsIdentity identity)
        => new() { User = new ClaimsPrincipal(identity) };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Fixture(MemoryEntities entities, TimeProvider? clock = null)
    {
        private readonly DiscussionStore _store = new(entities, new NoDiscussions());
        private readonly InstallationOwner _owner = new(entities, clock);
        public MemoryEntities Entities => entities;

        public void Discussion(string id, string? owner, bool confidential = false)
        {
            var data = new JsonObject
            {
                ["discussion_id"] = id,
                ["app"] = "nova",
                ["type"] = "chat",
                ["status"] = "idle",
                ["title"] = id,
                ["agent"] = "agent-nova",
                ["confidential"] = confidential,
            };
            if (owner is not null) data["owner_id"] = owner;
            entities.Items.Add(new LeafEntity(Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), id, data,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
        }

        /// <summary>What Nova's route filter does before every endpoint.</summary>
        public async Task<DefaultHttpContext> Stamped(DefaultHttpContext context)
        {
            await _owner.StampAsync(context);
            return context;
        }

        public async Task<bool> CanRead(string id, HttpContext context)
            => DiscussionAccessPolicy.CanRead((await _store.GetAsync(id))!, context);

        // The same filter GET /discussions applies to the open list.
        public async Task<string[]> Listed(HttpContext context)
            => (await _store.ListOpenAsync())
                .Where(d => DiscussionAccessPolicy.CanRead(d, context))
                .Select(d => d.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];

        public void User(Guid id, DateTimeOffset createdAt)
            => Items.Add(new LeafEntity(id, "user", id.ToString(), id.ToString(), [], createdAt, createdAt, "test"));

        public void SetupOwner(string ownerUserId)
        {
            Items.RemoveAll(e => e.TypeSlug == "setup-status");
            Items.Add(new LeafEntity(Guid.NewGuid(), "setup-status", "default", "Setup Status",
                new JsonObject { ["experience"] = new JsonObject { ["ownerUserId"] = ownerUserId } },
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "system:setup"));
        }

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
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoDiscussions : IDiscussions
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
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();
        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
