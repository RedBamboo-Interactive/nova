using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Personal context as a discussion setting: chosen at creation or later through
/// PUT /discussions/{id}/personal-context by whoever may modify the discussion, shown on the
/// discussion with where its value comes from, and announced with discussion.changed.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    [Fact]
    public async Task A_discussion_opened_for_life_admin_can_ask_for_personal_context()
    {
        using var world = new World("session-1");

        var (_, chosen) = await OpenAsync(world, Person(), new CreateDiscussionRequest { Title = "Life admin", PersonalContext = true });
        var (_, plain) = await OpenAsync(world, Person(), new CreateDiscussionRequest { Title = "CC3 assets" });
        var (_, signed) = await OpenAsync(world, SignedAgent(world), new CreateDiscussionRequest { Title = "Agent work", PersonalContext = false });

        Assert.True(chosen.GetProperty("personalContext").GetBoolean());
        Assert.Equal("explicit", chosen.GetProperty("personalContextSource").GetString());
        Assert.False(plain.GetProperty("personalContext").GetBoolean());
        Assert.Equal("default", plain.GetProperty("personalContextSource").GetString());
        Assert.False(signed.GetProperty("personalContext").GetBoolean());
        Assert.Equal("explicit", signed.GetProperty("personalContextSource").GetString());
        // Stored on the record, so the first turn already has it.
        var stored = await world.Discussions.GetAsync(chosen.GetProperty("id").GetString()!);
        Assert.True(stored!.PersonalContextSetting);
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData("chat", false)]
    public async Task Unset_shows_the_type_default(string type, bool expected)
    {
        using var world = new World("session-1", type);

        var info = Info(world.Discussion());

        Assert.Equal(expected, info.GetProperty("personalContext").GetBoolean());
        Assert.Equal("default", info.GetProperty("personalContextSource").GetString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_person_turns_it_on_and_off_and_null_returns_to_the_default()
    {
        using var world = new World("session-1", "live");

        var (offStatus, off) = await SetAsync(world, Person(), "false");
        var (_, on) = await SetAsync(world, Person(), "true");
        var (_, reset) = await SetAsync(world, Person(), "null");

        Assert.Equal(200, offStatus);
        Assert.False(off.GetProperty("personalContext").GetBoolean());         // an explicit off beats LIVE's on
        Assert.Equal("explicit", off.GetProperty("personalContextSource").GetString());
        Assert.True(on.GetProperty("personalContext").GetBoolean());
        Assert.True(reset.GetProperty("personalContext").GetBoolean());        // LIVE's default again
        Assert.Equal("default", reset.GetProperty("personalContextSource").GetString());
        Assert.Null(world.Discussion().PersonalContextSetting);
    }

    [Fact]
    public async Task The_owning_agent_may_set_it_where_it_may_modify_the_discussion()
    {
        using var world = new World("session-1");

        var (status, body) = await SetAsync(world, SignedAgent(world), "true");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("personalContext").GetBoolean());
        Assert.True(world.Discussion().PersonalContext);
    }

    [Fact]
    public async Task Someone_who_may_not_modify_the_discussion_is_refused_and_nothing_changes()
    {
        using var world = new World("session-1");
        AddOrigin(world, confidential: true);
        var confidential = (await world.Discussions.GetAsync(Origin))!;
        world.Store.CreateDiscussion("Laurent's", AgentOf(world), new JsonObject
        {
            ["discussion_id"] = "laurent01", ["app"] = "nova", ["type"] = "chat", ["owner_id"] = "laurent",
        });
        var someoneElses = (await world.Discussions.GetAsync("laurent01"))!;

        var (otherPerson, _) = await SetAsync(world, Person(sub: "someone-else"), "true", discussion: someoneElses);
        var (foreignAgent, _) = await SetAsync(world, Signed("agent", "11111111-2222-3333-4444-555555555555"), "true",
            discussion: confidential);
        var (owner, _) = await SetAsync(world, Person(sub: "laurent"), "true", discussion: someoneElses);

        Assert.Equal(403, otherPerson);
        Assert.Equal(404, foreignAgent);                            // a confidential discussion isn't even admitted to exist
        Assert.Equal(200, owner);                                   // the same request from its owner goes through
        Assert.Null((await world.Discussions.GetAsync(Origin))!.PersonalContextSetting);
        Assert.Single(world.Published, e => e.Name == "discussion.changed");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"personalContext":"yes"}""")]
    [InlineData("""{"personalContext":1}""")]
    public async Task A_missing_or_non_boolean_value_is_a_400(string body)
    {
        using var world = new World("session-1");

        var request = JsonSerializer.Deserialize<DiscussionPersonalContextRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await DiscussionEndpoints.SetPersonalContextAsync(world.Discussion(), request, ContextOf(Person()),
            world.Discussions, world.Events);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Null(world.Discussion().PersonalContextSetting);
    }

    [Fact]
    public async Task Clients_are_told_once_when_the_setting_really_changes()
    {
        using var world = new World("session-1");

        await SetAsync(world, Person(), "true");
        await SetAsync(world, Person(), "true");
        await SetAsync(world, Person(), "false");

        var changed = world.Published.Where(e => e.Name == "discussion.changed").ToList();
        Assert.Equal(2, changed.Count);
        Assert.All(changed, e => Assert.Equal("race1234", e.Data!["discussionId"]!.GetValue<string>()));
    }

    private static async Task<(int Status, JsonElement Body)> SetAsync(World world, ClaimsPrincipal user, string value,
        DiscussionRead? discussion = null)
    {
        var request = JsonSerializer.Deserialize<DiscussionPersonalContextRequest>(
            $$"""{"personalContext":{{value}}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await DiscussionEndpoints.SetPersonalContextAsync(discussion ?? world.Discussion(), request,
            ContextOf(user), world.Discussions, world.Events);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, result is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default);
    }

    private static JsonElement Info(DiscussionRead discussion) => JsonSerializer.SerializeToElement(DiscussionStore.ToInfo(discussion));
}
