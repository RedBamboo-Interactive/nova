using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// The LIVE timeline switch as a discussion setting: chosen at creation or later through
/// PUT /discussions/{id}/live-timeline by whoever may access the discussion, shown on the discussion
/// with where its value comes from, and announced with discussion.changed. And GET /live/timeline,
/// where a discussion with the switch off reads the events when it needs them.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    [Fact]
    public async Task A_discussion_opened_for_life_admin_can_ask_for_the_live_timeline()
    {
        using var world = new World("session-1");

        var (_, chosen) = await OpenAsync(world, Person(), new CreateDiscussionRequest { Title = "Life admin", LiveTimeline = true });
        var (_, plain) = await OpenAsync(world, Person(), new CreateDiscussionRequest { Title = "CC3 assets" });
        var (_, signed) = await OpenAsync(world, SignedAgent(world), new CreateDiscussionRequest { Title = "Agent work", LiveTimeline = false });

        Assert.True(chosen.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("explicit", chosen.GetProperty("liveTimelineSource").GetString());
        Assert.False(plain.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("default", plain.GetProperty("liveTimelineSource").GetString());
        Assert.False(signed.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("explicit", signed.GetProperty("liveTimelineSource").GetString());
        // Stored on the record under its own key, so the first turn already has it.
        var stored = await world.Discussions.GetAsync(chosen.GetProperty("id").GetString()!);
        Assert.True(stored!.LiveTimelineSetting);
        Assert.False(plain.TryGetProperty("personalContext", out _));
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData(HeartbeatService.DiscussionType, true)]
    [InlineData("chat", false)]
    public async Task Unset_shows_the_type_default(string type, bool expected)
    {
        using var world = new World("session-1", type);

        var info = Info(world.Discussion());

        Assert.Equal(expected, info.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("default", info.GetProperty("liveTimelineSource").GetString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_person_turns_it_off_and_on_and_null_returns_to_the_default()
    {
        using var world = new World("session-1", "live");

        var (offStatus, off) = await SetAsync(world, Person(), "false");
        var (_, on) = await SetAsync(world, Person(), "true");
        var (_, reset) = await SetAsync(world, Person(), "null");

        Assert.Equal(200, offStatus);
        Assert.False(off.GetProperty("liveTimeline").GetBoolean());          // an explicit off beats LIVE's on
        Assert.Equal("explicit", off.GetProperty("liveTimelineSource").GetString());
        Assert.True(on.GetProperty("liveTimeline").GetBoolean());
        Assert.True(reset.GetProperty("liveTimeline").GetBoolean());         // LIVE's default again
        Assert.Equal("default", reset.GetProperty("liveTimelineSource").GetString());
        Assert.Null(world.Discussion().LiveTimelineSetting);
        Assert.Null(world.Entity.Data["live_timeline"]);
    }

    [Fact]
    public async Task Turning_on_a_chat_beats_its_default_off()
    {
        using var world = new World("session-1");

        var (_, on) = await SetAsync(world, Person(), "true");

        Assert.True(on.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("explicit", on.GetProperty("liveTimelineSource").GetString());
    }

    [Fact]
    public async Task A_choice_stored_under_the_old_key_is_honoured_and_replaced_on_the_next_write()
    {
        using var world = new World("session-1");
        await world.Store.PatchAsync(world.Store.DiscussionId, new JsonObject { ["personal_context"] = true });

        Assert.True(world.Discussion().LiveTimeline);
        Assert.Equal("explicit", Info(world.Discussion()).GetProperty("liveTimelineSource").GetString());

        var (_, reset) = await SetAsync(world, Person(), "null");

        // Null means the default: the old key must not keep answering in its place.
        Assert.False(reset.GetProperty("liveTimeline").GetBoolean());
        Assert.Equal("default", reset.GetProperty("liveTimelineSource").GetString());
        Assert.Null(world.Entity.Data["personal_context"]);
    }

    [Fact]
    public async Task The_owning_agent_may_set_it_where_it_may_access_the_discussion()
    {
        using var world = new World("session-1");

        var (status, body) = await SetAsync(world, SignedAgent(world), "true");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("liveTimeline").GetBoolean());
        Assert.True(world.Discussion().LiveTimeline);
    }

    [Fact]
    public async Task Someone_who_may_not_access_the_discussion_is_refused_and_nothing_changes()
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
        Assert.Null((await world.Discussions.GetAsync(Origin))!.LiveTimelineSetting);
        Assert.Single(world.Published, e => e.Name == "discussion.changed");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"liveTimeline":"yes"}""")]
    [InlineData("""{"liveTimeline":1}""")]
    [InlineData("""{"personalContext":true}""")]                // the old field is not an alias
    public async Task A_missing_or_non_boolean_value_is_a_400(string body)
    {
        using var world = new World("session-1");

        var request = JsonSerializer.Deserialize<DiscussionLiveTimelineRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await DiscussionEndpoints.SetLiveTimelineAsync(world.Discussion(), request, ContextOf(Person()),
            world.Discussions, world.Events);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Null(world.Discussion().LiveTimelineSetting);
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

    // ── GET /live/timeline ─────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_timeline_lists_the_live_events_newest_last_with_the_text_a_context_line_shows()
    {
        using var world = new World("session-1", "live");
        var live = world.Store.DiscussionId;
        await PostEventAsync(world, live, "event:automation", "Morning brief completed", 0);
        await world.Stream.PostAsync(live, "user", "Good morning", new JsonObject { ["source"] = "user-message" });
        await PostEventAsync(world, live, "event:mail", "New mail From: Revolut / Subject: Your account is restricted", 1);
        await PostEventAsync(world, live, "event:some-new-extension", "Something only a future extension says", 2);

        var (status, body) = await TimelineAsync(world, Person(), "");

        Assert.Equal(200, status);
        Assert.Equal("race1234", body.GetProperty("discussionId").GetString());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["automation", "mail", "some-new-extension"], events.Select(e => e.GetProperty("source").GetString()));
        Assert.Equal(T0.AddMinutes(2), events[^1].GetProperty("timestamp").GetDateTime().ToUniversalTime());
        // Exactly the line the agent would have read in a context with the timeline on.
        var full = NovaContextBuilder.BuildFullContext(
            NovaContextBuilder.BuildSnapshot([], null, null, null,
                liveEvents: LiveTimelineContext.Select(world.Stream.Snapshot(), on: true)),
            "race1234", T0.AddMinutes(2), NovaContextFrameTests.Device(), "text", "Nova", []);
        foreach (var e in events)
            Assert.Contains($"\n- [{e.GetProperty("source").GetString()}] {e.GetProperty("content").GetString()} (", full);
    }

    [Fact]
    public async Task The_timeline_pages_backwards_and_filters()
    {
        using var world = new World("session-1", "live");
        var live = world.Store.DiscussionId;
        for (var i = 0; i < 20; i++)
            await PostEventAsync(world, live, i % 2 == 0 ? "event:automation" : "event:mail", $"event {i}", i);

        var (_, latest) = await TimelineAsync(world, Person(), "?limit=5");
        var first = Contents(latest);
        var (_, older) = await TimelineAsync(world, Person(),
            $"?limit=5&before={Uri.EscapeDataString(latest.GetProperty("events")[0].GetProperty("timestamp").GetString()!)}");
        var (_, defaults) = await TimelineAsync(world, Person(), "");
        var (_, since) = await TimelineAsync(world, Person(), $"?since={Uri.EscapeDataString(T0.AddMinutes(16).ToString("O"))}");
        var (_, mail) = await TimelineAsync(world, Person(), "?source=mail&limit=3");
        var (_, all) = await TimelineAsync(world, Person(), "?limit=100");

        Assert.Equal(["event 15", "event 16", "event 17", "event 18", "event 19"], first);
        Assert.True(latest.GetProperty("hasMore").GetBoolean());
        Assert.Equal(["event 10", "event 11", "event 12", "event 13", "event 14"], Contents(older));
        Assert.Equal(Enumerable.Range(5, 15).Select(i => $"event {i}"), Contents(defaults));   // 15 by default
        Assert.Equal(["event 17", "event 18", "event 19"], Contents(since));                    // since is exclusive
        Assert.False(since.GetProperty("hasMore").GetBoolean());
        Assert.Equal(["event 15", "event 17", "event 19"], Contents(mail));
        Assert.All(mail.GetProperty("events").EnumerateArray(), e => Assert.Equal("mail", e.GetProperty("source").GetString()));
        Assert.Equal(20, Contents(all).Count);
        Assert.False(all.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=many")]
    [InlineData("?before=yesterday")]
    [InlineData("?since=soon")]
    public async Task A_bad_timeline_query_is_a_400(string query)
    {
        using var world = new World("session-1", "live");

        var (status, _) = await TimelineAsync(world, Person(), query);

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task The_timeline_is_read_as_its_live_discussion_is()
    {
        using var world = new World("session-1");
        world.Store.CreateDiscussion("Laurent's LIVE", AgentOf(world), new JsonObject
        {
            ["discussion_id"] = "livelrnt", ["app"] = "nova", ["type"] = "live", ["owner_id"] = "laurent",
        });
        var laurents = (await world.Discussions.GetAsync("livelrnt"))!;
        await PostEventAsync(world, laurents.EntityId, "event:mail", "Laurent's mail", 0);

        var (owner, body) = await TimelineAsync(world, Person(sub: "laurent"), "");
        var (agent, _) = await TimelineAsync(world, Signed("agent", AgentOf(world), sub: "laurent"), "");
        var (otherPerson, _) = await TimelineAsync(world, Person(sub: "someone-else"), "");

        Assert.Equal(200, owner);
        Assert.Equal(["Laurent's mail"], Contents(body));
        Assert.Equal(200, agent);                                   // his Agent, acting for him
        Assert.Equal(403, otherPerson);                             // as GET /discussions/livelrnt would answer
    }

    [Fact]
    public async Task A_confidential_live_is_not_admitted_to_exist_and_none_at_all_is_a_404()
    {
        using var empty = new World("session-1");
        var (none, _) = await TimelineAsync(empty, Person(), "");

        using var world = new World("session-1");
        world.Store.CreateDiscussion("Private LIVE", AgentOf(world), new JsonObject
        {
            ["discussion_id"] = "liveconf", ["app"] = "nova", ["type"] = "live", ["owner_id"] = Owner, ["confidential"] = true,
        });
        var (foreignAgent, _) = await TimelineAsync(world, Signed("agent", "11111111-2222-3333-4444-555555555555"), "");

        Assert.Equal(404, none);
        Assert.Equal(404, foreignAgent);
    }

    [Fact]
    public async Task On_an_install_with_several_people_each_reads_their_own_live()
    {
        using var world = new World("session-1");
        foreach (var (id, owner) in new[] { ("livelrnt", "laurent"), ("livebob1", "bob") })
        {
            world.Store.CreateDiscussion($"{owner}'s LIVE", AgentOf(world), new JsonObject
            {
                ["discussion_id"] = id, ["app"] = "nova", ["type"] = "live", ["owner_id"] = owner,
            });
            await PostEventAsync(world, (await world.Discussions.GetAsync(id))!.EntityId, "event:mail", $"{owner}'s mail", 0);
        }

        var (_, laurent) = await TimelineAsync(world, Person(sub: "laurent"), "");
        var (_, bob) = await TimelineAsync(world, Person(sub: "bob"), "");

        Assert.Equal(["laurent's mail"], Contents(laurent));
        Assert.Equal(["bob's mail"], Contents(bob));
        Assert.Equal("livebob1", bob.GetProperty("discussionId").GetString());
    }

    private static Task PostEventAsync(World world, Guid live, string source, string content, int minutes)
        => world.Stream.PostAsync(live, "system", content, new JsonObject
        {
            ["source"] = source, ["timestamp"] = T0.AddMinutes(minutes).ToString("O"),
        });

    private static async Task<(int Status, JsonElement Body)> TimelineAsync(World world, ClaimsPrincipal user, string query)
    {
        var ctx = ContextOf(user);
        ctx.Request.QueryString = new QueryString(query);
        var result = await DiscussionEndpoints.ReadLiveTimelineAsync(ctx, world.Discussions, world.Stream, world.Agents);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, result is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default);
    }

    private static List<string?> Contents(JsonElement body)
        => body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("content").GetString()).ToList();

    private static async Task<(int Status, JsonElement Body)> SetAsync(World world, ClaimsPrincipal user, string value,
        DiscussionRead? discussion = null)
    {
        var request = JsonSerializer.Deserialize<DiscussionLiveTimelineRequest>(
            $$"""{"liveTimeline":{{value}}}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await DiscussionEndpoints.SetLiveTimelineAsync(discussion ?? world.Discussion(), request,
            ContextOf(user), world.Discussions, world.Events);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, result is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default);
    }

    private static JsonElement Info(DiscussionRead discussion) => JsonSerializer.SerializeToElement(DiscussionStore.ToInfo(discussion));
}
