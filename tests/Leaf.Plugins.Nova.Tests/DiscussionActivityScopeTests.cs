using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Discussion-activity lines (title and excerpt) reach a LIVE only from the same Agent and the
/// same person. Another Agent's discussions, another person's, every Agent's LIVE and confidential
/// threads post nothing, whatever their <c>live</c> setting.
/// </summary>
public sealed class DiscussionActivityScopeTests
{
    private const string Owner = "11111111-1111-1111-1111-111111111111";
    private const string Stranger = "22222222-2222-2222-2222-222222222222";
    private static readonly string Nova = Guid.NewGuid().ToString();
    private static readonly string Sibylle = Guid.NewGuid().ToString();

    // ---- the pure rule -------------------------------------------------------------------

    private static DiscussionRead Read(string id, string? agent, string? owner = Owner, string type = "chat",
        string? title = "Weekend plans", bool confidential = false, string status = "idle")
        => new(id, title, null, status, DateTime.UtcNow, DateTime.UtcNow, 0, null, owner, Guid.NewGuid(),
            agent, type, Confidential: confidential);

    private static readonly DiscussionRead NovaLive = Read("live", Nova, type: "live", title: "LIVE");

    [Fact]
    public void Same_agent_and_same_person_relays()
        => Assert.True(LiveEvents.RelaysActivity(Read("a", Nova), NovaLive, Nova, Owner));

    [Fact]
    public void Another_agents_discussion_is_never_relayed()
        => Assert.False(LiveEvents.RelaysActivity(Read("a", Sibylle), NovaLive, Nova, Owner));

    [Fact]
    public void Another_agents_live_is_never_relayed()
        => Assert.False(LiveEvents.RelaysActivity(Read("b", Sibylle, type: "live"), NovaLive, Nova, Owner));

    [Fact]
    public void Novas_own_live_is_never_relayed_into_itself()
        => Assert.False(LiveEvents.RelaysActivity(NovaLive, NovaLive, Nova, Owner));

    [Fact]
    public void A_second_nova_live_is_not_relayed_either()
        => Assert.False(LiveEvents.RelaysActivity(Read("old-live", Nova, type: "live"), NovaLive, Nova, Owner));

    [Fact]
    public void Confidential_stays_silent()
        => Assert.False(LiveEvents.RelaysActivity(Read("a", Nova, confidential: true), NovaLive, Nova, Owner));

    [Fact]
    public void Untitled_stays_silent()
        => Assert.False(LiveEvents.RelaysActivity(Read("a", Nova, title: ""), NovaLive, Nova, Owner));

    [Fact]
    public void Another_persons_discussion_with_the_same_agent_is_not_relayed()
        => Assert.False(LiveEvents.RelaysActivity(Read("a", Nova, owner: Stranger), NovaLive, Nova, Owner));

    [Fact]
    public void A_discussion_without_an_agent_is_novas()
    {
        Assert.True(LiveEvents.RelaysActivity(Read("a", null), NovaLive, Nova, Owner));
        Assert.False(LiveEvents.RelaysActivity(Read("a", null), Read("live", Sibylle, type: "live"), Nova, Owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local-user")]
    public void Unowned_and_local_discussions_are_the_installation_owners(string? owner)
    {
        Assert.True(LiveEvents.RelaysActivity(Read("a", Nova, owner: owner), NovaLive, Nova, Owner));
        Assert.False(LiveEvents.RelaysActivity(Read("a", Nova, owner: owner),
            Read("live", Nova, owner: Stranger, type: "live"), Nova, Owner));
    }

    [Fact]
    public void A_closed_live_receives_nothing()
        => Assert.False(LiveEvents.RelaysActivity(Read("a", Nova),
            Read("live", Nova, type: "live", status: DiscussionStatus.Archived), Nova, Owner));

    // ---- end to end, through DiscussionActivity and the real LIVE --------------------------

    [Fact]
    public async Task Novas_own_discussion_posts_under_her_agent_name()
    {
        using var f = new Fixture();
        var mine = await f.AddDiscussionAsync("mine", Nova, title: "Weekend plans");

        await f.Activity.OnUserMessage(mine, "Shall we go hiking?", "Laurent");
        await f.Activity.OnNovaMessage(mine, "Absolutely, Saturday.");
        await f.Activity.OnArchived(mine);

        Assert.Equal(
            ["Laurent in \"Weekend plans\": Shall we go hiking?",
             "Nova in \"Weekend plans\": Absolutely, Saturday.",
             "\"Weekend plans\" archived"],
            f.LiveMessages.Select(m => m.Content));
    }

    [Fact]
    public async Task Agent_activity_uses_the_live_agents_name()
    {
        using var f = new Fixture(novaName: "Stella");
        var mine = await f.AddDiscussionAsync("mine", Nova, title: "Weekend plans");

        await f.Activity.OnNovaMessage(mine, "Absolutely, Saturday.");

        Assert.Equal("Stella in \"Weekend plans\": Absolutely, Saturday.", Assert.Single(f.LiveMessages).Content);
    }

    [Fact]
    public async Task Another_agents_discussion_produces_nothing_in_novas_live()
    {
        using var f = new Fixture();
        var theirs = await f.AddDiscussionAsync("theirs", Sibylle, title: "Private project");

        await f.Activity.OnUserMessage(theirs, "the secret part", "Laurent");
        await f.Activity.OnNovaMessage(theirs, "the secret answer");
        await f.Activity.OnArchived(theirs);
        await f.Activity.OnReopened(theirs);

        Assert.Empty(f.LiveMessages);
    }

    [Fact]
    public async Task Another_agents_live_is_not_relayed()
    {
        using var f = new Fixture();
        var theirLive = await f.AddDiscussionAsync("sibylle-live", Sibylle, type: "live", title: "LIVE");

        await f.Activity.OnUserMessage(theirLive, "hello Sibylle", "Laurent");
        await f.Activity.OnNovaMessage(theirLive, "hello Laurent");

        Assert.Empty(f.LiveMessages);
    }

    [Fact]
    public async Task Confidential_stays_silent_end_to_end()
    {
        using var f = new Fixture();
        var secret = await f.AddDiscussionAsync("secret", Nova, title: "Health", confidential: true);

        await f.Activity.OnUserMessage(secret, "symptoms", "Laurent");
        await f.Activity.OnNovaMessage(secret, "advice");
        await f.Activity.OnArchived(secret);

        Assert.Empty(f.LiveMessages);
    }

    [Fact]
    public async Task Another_persons_discussion_produces_nothing_in_the_owners_live()
    {
        using var f = new Fixture();
        var guest = await f.AddDiscussionAsync("guest", Nova, title: "Guest chat", owner: Stranger);

        await f.Activity.OnUserMessage(guest, "hi", "Guest");

        Assert.Empty(f.LiveMessages);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<LeafEntity> all = [];
        private readonly List<DiscussionMessage> records = [];
        private readonly AgentDirectory agents;
        private readonly LeafEntity live;
        public DiscussionStore Store { get; }
        public DiscussionActivity Activity { get; }

        public IEnumerable<DiscussionMessage> LiveMessages
        {
            get { lock (records) return records.Where(m => m.DiscussionId == live.Id).ToArray(); }
        }

        public Fixture(string novaName = "Nova")
        {
            Add("agent", "nova", new(), Guid.Parse(Nova), novaName);
            Add("agent", "sibylle", new(), Guid.Parse(Sibylle), "Sibylle");
            Add("setup-status", "default", new() { ["experience"] = new JsonObject { ["ownerUserId"] = Owner } });
            Add("plugin", "nova", new());
            live = Add("discussion", DiscussionStore.Slug("live"), new()
            {
                ["discussion_id"] = "live", ["app"] = "nova", ["type"] = "live", ["status"] = "idle",
                ["agent"] = Nova, ["owner_id"] = Owner, ["title"] = "LIVE",
            });

            var entities = CallbackDeliveryTests.Proxy.Create<IEntityStore>((method, args) => method switch
            {
                "GetBySlugAsync" => Task.FromResult(Locked(() => args.Length > 1 && args[1] is string typed
                    ? all.FirstOrDefault(e => e.TypeSlug == (string)args[0]! && e.Slug == typed)
                    : all.FirstOrDefault(e => e.Slug == (string)args[0]!))),
                "GetAsync" => Task.FromResult(Locked(() => all.FirstOrDefault(e => e.Id == (Guid)args[0]!))),
                "QueryAsync" => Task.FromResult<IReadOnlyList<LeafEntity>>(Locked(() => Query((EntityQuery)args[0]!))),
                "PatchAsync" => Task.FromResult(Patch((Guid)args[0]!, (JsonObject)args[1]!)),
                _ => throw new NotSupportedException(method),
            });
            var discussions = CallbackDeliveryTests.Proxy.Create<IDiscussions>((method, args) =>
            {
                if (method == "PostAsync")
                {
                    lock (records) records.Add(new(records.Count + 1, (Guid)args[0]!, (string)args[1]!,
                        (string)args[2]!, (JsonObject)args[3]!, DateTimeOffset.UtcNow));
                    return Task.CompletedTask;
                }
                if (method == "GetMessagesAsync")
                    lock (records) return Task.FromResult<IReadOnlyList<DiscussionMessage>>(
                        records.Where(m => m.DiscussionId == (Guid)args[0]! && m.Id > (long)args[2]!).ToArray());
                throw new NotSupportedException(method);
            });
            var events = CallbackDeliveryTests.Proxy.Create<IPluginEvents>((method, _) =>
                method == "Subscribe" ? new Noop() : Task.CompletedTask);
            agents = new AgentDirectory(entities, events) { NovaAgentId = Nova };
            var client = new RedComputeClient(new CallbackDeliveryTests.Gateway());
            Store = new DiscussionStore(entities, discussions);
            var injector = new EventInjector(discussions, events, client, agents, entities,
                new ConversationUnread(Store, discussions, client));
            Activity = new DiscussionActivity(new LiveEvents(Store, injector, agents, new InstallationOwner(entities)));
        }

        public async Task<DiscussionRead> AddDiscussionAsync(string id, string agent, string type = "chat",
            string title = "Weekend plans", bool confidential = false, string owner = Owner)
        {
            Add("discussion", DiscussionStore.Slug(id), new()
            {
                ["discussion_id"] = id, ["app"] = "nova", ["type"] = type, ["status"] = "idle",
                ["agent"] = agent, ["owner_id"] = owner, ["title"] = title, ["confidential"] = confidential,
                ["live"] = true,
            });
            return (await Store.GetAsync(id))!;
        }

        private LeafEntity Add(string type, string slug, JsonObject data, Guid? id = null, string? name = null)
        {
            var entity = new LeafEntity(id ?? Guid.NewGuid(), type, slug, name ?? slug, data,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
            lock (all) all.Add(entity);
            return entity;
        }

        private T Locked<T>(Func<T> read) { lock (all) return read(); }

        private LeafEntity[] Query(EntityQuery query)
            => all.Where(e => e.TypeSlug == query.TypeSlug
                    && (query.AfterId is null || e.Id.CompareTo(query.AfterId.Value) > 0)
                    && (query.DataEquals is null || query.DataEquals.All(kv => Matches(e.Data[kv.Key], kv.Value))))
                .OrderBy(e => e.Id)
                .Skip(query.Offset)
                .Take(query.Limit > 0 ? query.Limit : int.MaxValue)
                .ToArray();

        private static bool Matches(JsonNode? node, object? expected)
            => expected is null ? node is null
                : node is JsonValue value && value.ToJsonString() == JsonSerializer.Serialize(expected);

        private LeafEntity Patch(Guid id, JsonObject patch)
        {
            lock (all)
            {
                var index = all.FindIndex(e => e.Id == id);
                var data = all[index].Data.DeepClone().AsObject();
                foreach (var (key, value) in patch) data[key] = value?.DeepClone();
                return all[index] = all[index] with { Data = data };
            }
        }

        public void Dispose() => agents.Dispose();
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
}
