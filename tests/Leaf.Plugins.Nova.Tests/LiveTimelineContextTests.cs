using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Whether a discussion's <nova-context> carries the LIVE timeline. On 2026-11-10 a coding discussion
// (fca95f0f) was handed a Revolut "account restricted" mail notice and a Heartbeat note meant for
// LIVE, and the worker raised Laurent's bank account mid-task. Each discussion has one switch,
// "LIVE timeline": on, exactly the old behavior (every source, the latest 15); off, no LIVE event at
// all, and the full context says where to read them. On by default for LIVE and Heartbeat, off for
// the rest; an explicit choice wins both ways and null returns to the default.
public sealed class LiveTimelineContextTests
{
    private static readonly DateTime Now = NovaContextFrameTests.Now;

    private const string Mail = "New mail From: Revolut / Subject: Your account is restricted";
    private const string HeartbeatNote = "Revolut sent a second account restricted mail; check in ten minutes";
    private const string AgentNote = "Note from Gemma: Laurent seemed stressed this morning";
    private const string FutureSource = "A future extension says something personal";
    private const string Music = "Now playing in Kitchen: Holocene — Bon Iver";

    private const string Recipe =
        "Read the LIVE timeline: curl -s \"http://127.0.0.1:18804/api/apps/nova/live/timeline?limit=15\" -H \"Authorization: Bearer $REDLEAF_EXECUTION_TOKEN\"";

    // One of every kind, oldest first, with a person's message in between that is not an event.
    private static List<DiscussionMessage> Timeline() =>
    [
        Event(1, "event:automation", "Morning brief completed", -20),
        Event(2, "event:mail", Mail, -19),
        Event(3, "event:callback", "Code session 3f2a finished: tests green", -18),
        Event(4, "event:heartbeat", HeartbeatNote, -17),
        Event(5, "event:discussion", "Laurent in \"CC3 assets\": export the atlas", -16),
        new(6, Guid.Empty, "user", "Good morning", new JsonObject { ["source"] = "user-message", ["uid"] = "u1" },
            new DateTimeOffset(Now.AddMinutes(-15))),
        Event(7, "event:agent-note", AgentNote, -14),
        Event(8, "event:presence", "Laurent is at the office", -13),
        Event(9, "event:some-new-extension", FutureSource, -12),
        Event(10, "event:device", "Switched to Desktop", -11),
        Event(11, "event:sonos", Music, -10),
    ];

    private static readonly string[] EveryEvent =
    [
        "[automation] Morning brief completed",
        $"[mail] {Mail}",
        "[callback] Code session 3f2a finished: tests green",
        $"[heartbeat] {HeartbeatNote}",
        "[discussion] Laurent in \"CC3 assets\": export the atlas",
        $"[agent-note] {AgentNote}",
        "[presence] Laurent is at the office",
        $"[some-new-extension] {FutureSource}",
        "[device] Switched to Desktop",
        $"[sonos] {Music}",
    ];

    // ── The setting ────────────────────────────────────────────────

    [Theory]
    [InlineData("live", true)]
    [InlineData(HeartbeatService.DiscussionType, true)]
    [InlineData("chat", false)]
    [InlineData("code", false)]
    [InlineData("Live", false)]        // types are exact: only "live" is the LIVE timeline
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Unset_follows_the_type(string? type, bool expected)
    {
        Assert.Equal(expected, LiveTimelineContext.DefaultFor(type));
        Assert.Equal(expected, (ChatEntry() with { Type = type! }).LiveTimeline);
    }

    [Theory]
    [InlineData("chat", true)]                              // life admin in an ordinary discussion
    [InlineData("chat", false)]
    [InlineData("live", false)]                             // a LIVE that keeps its context quiet
    [InlineData("live", true)]
    [InlineData(HeartbeatService.DiscussionType, false)]
    public void An_explicit_choice_wins_over_the_type_both_ways(string type, bool chosen)
    {
        var discussion = Discussion(type, chosen);

        Assert.Equal(chosen, discussion.LiveTimeline);
        Assert.Equal(chosen, LiveTimelineContext.IsOn(discussion));
    }

    [Fact]
    public void The_choice_is_read_from_the_record_and_absent_or_null_means_default()
    {
        var chat = DiscussionStore.Map(Entity("chat", new JsonObject { ["live_timeline"] = true }))!;
        var live = DiscussionStore.Map(Entity("live", new JsonObject { ["live_timeline"] = false }))!;
        var unset = DiscussionStore.Map(Entity("live", new JsonObject()))!;
        var cleared = DiscussionStore.Map(Entity("chat", new JsonObject { ["live_timeline"] = null }))!;

        Assert.Equal((true, true), (chat.LiveTimelineSetting, chat.LiveTimeline));
        Assert.Equal((false, false), (live.LiveTimelineSetting, live.LiveTimeline));
        Assert.Equal((null, true), (unset.LiveTimelineSetting, unset.LiveTimeline));
        Assert.Equal((null, false), (cleared.LiveTimelineSetting, cleared.LiveTimeline));
    }

    [Fact]
    public void A_record_holding_only_the_old_personal_context_key_is_read_through_it()
    {
        var chat = DiscussionStore.Map(Entity("chat", new JsonObject { ["personal_context"] = true }))!;
        var live = DiscussionStore.Map(Entity("live", new JsonObject { ["personal_context"] = false }))!;
        // The current key wins when both are there.
        var both = DiscussionStore.Map(Entity("chat", new JsonObject { ["live_timeline"] = false, ["personal_context"] = true }))!;

        Assert.Equal((true, true), (chat.LiveTimelineSetting, chat.LiveTimeline));
        Assert.Equal((false, false), (live.LiveTimelineSetting, live.LiveTimeline));
        Assert.Equal((false, false), (both.LiveTimelineSetting, both.LiveTimeline));
    }

    // ── What each state sees ───────────────────────────────────────

    [Fact]
    public void On_keeps_every_event_exactly_as_before_unknown_sources_included()
    {
        var timeline = Timeline();

        var events = LiveTimelineContext.Select(timeline, on: true);

        // The selection MessagePipeline made before the switch existed.
        var before = timeline
            .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
            .TakeLast(15)
            .ToList();
        Assert.Equal(before.Select(m => m.Metadata["source"]!.GetValue<string>()), events.Select(e => e.Source));
        Assert.Equal(before.Select(m => m.Content), events.Select(e => e.Content));
        Assert.Equal(before.Select(m => m.CreatedAt.UtcDateTime), events.Select(e => e.Timestamp));
        Assert.Contains(events, e => e.Source == "event:some-new-extension");
    }

    [Fact]
    public void On_lists_the_latest_fifteen_whatever_their_source()
    {
        var timeline = Enumerable.Range(0, 20).Select(i => Event(i + 1, "event:automation", $"run {i}", -100 + i))
            .Concat(Enumerable.Range(0, 30).Select(i => Event(i + 21, "event:mail", $"mail {i}", -50 + i)))
            .ToList();

        Assert.Equal(Enumerable.Range(15, 15).Select(i => $"mail {i}"),
            LiveTimelineContext.Select(timeline, on: true).Select(e => e.Content));
    }

    [Fact]
    public void Off_lists_nothing_at_all()
        => Assert.Empty(LiveTimelineContext.Select(Timeline(), on: false));

    [Fact]
    public void Only_event_records_are_events()
    {
        var records = new List<DiscussionMessage>
        {
            Event(1, "automation", "not an event record", -3),
            Event(2, "user-message", "hello", -2),
            new(3, Guid.Empty, "system", "no source", new JsonObject(), new DateTimeOffset(Now.AddMinutes(-1))),
            Event(4, "event:mail", Mail, 0),
        };

        Assert.Equal([Mail], LiveTimelineContext.Select(records, on: true).Select(e => e.Content));
    }

    [Fact]
    public void An_event_keeps_its_own_timestamp_when_it_carries_one()
    {
        var at = Now.AddMinutes(-42);
        var record = Event(1, "event:automation", "done", -1);
        record.Metadata["timestamp"] = new DateTimeOffset(at).ToString("O");

        Assert.Equal(at, LiveTimelineContext.Select([record], on: true).Single().Timestamp);
    }

    // ── Full context ───────────────────────────────────────────────

    [Fact]
    public void Full_context_with_the_timeline_on_lists_every_event_and_no_recipe()
    {
        var full = Full(Snapshot(LiveTimelineContext.Select(Timeline(), on: true), on: true));

        Assert.Contains("Recent LIVE events:", full);
        foreach (var line in EveryEvent) Assert.Contains(line, full);
        Assert.DoesNotContain("Read the LIVE timeline", full);
    }

    [Fact]
    public void Full_context_with_the_timeline_off_lists_no_event_and_says_where_they_are()
    {
        var full = Full(Snapshot(LiveTimelineContext.Select(Timeline(), on: false), on: false));

        Assert.DoesNotContain("Recent LIVE events:", full);
        foreach (var line in EveryEvent) Assert.DoesNotContain(line, full);
        Assert.DoesNotContain("Revolut", full);
        // One line, inside the recipe block, right after the other recipes.
        Assert.Single(full.Split('\n'), l => l == Recipe);
        var lines = full.Split('\n').ToList();
        Assert.StartsWith("React to a message", lines[lines.IndexOf(Recipe) - 1]);
    }

    [Fact]
    public void A_snapshot_from_before_the_switch_gets_no_recipe()
    {
        var old = Snapshot([], on: false);
        old.LiveTimeline = null;

        Assert.DoesNotContain("Read the LIVE timeline", Full(old));
    }

    [Fact]
    public void No_context_tag_carries_a_scope()
    {
        var on = Snapshot(LiveTimelineContext.Select(Timeline(), on: true), on: true);
        var off = Snapshot([], on: false);

        Assert.DoesNotContain("scope=", OpenTag(Full(on)));
        Assert.DoesNotContain("scope=", OpenTag(Full(off)));
        Assert.DoesNotContain("scope=", OpenTag(Delta(on, off)));
        Assert.DoesNotContain("scope=", OpenTag(Delta(off, on)));
        Assert.DoesNotContain("scope=", OpenTag(NovaContextBuilder.BuildReactionNote(
            ["Laurent reacted 👍 to your latest message: \"ok\""], Now, NovaContextFrameTests.Device(), "c0ffee01", "Nova",
            steered: false)));
    }

    // ── Changes since last message ─────────────────────────────────

    [Fact]
    public void Delta_with_the_timeline_on_reports_every_new_event()
    {
        var earlier = Timeline().Take(1).ToList();
        var later = earlier.Append(Event(20, "event:mail", Mail, -1))
            .Append(Event(21, "event:some-new-extension", FutureSource, 0)).ToList();

        var delta = Delta(
            Snapshot(LiveTimelineContext.Select(later, on: true), on: true),
            Snapshot(LiveTimelineContext.Select(earlier, on: true), on: true));

        Assert.Contains($"- [mail] {Mail}", delta);
        Assert.Contains($"- [some-new-extension] {FutureSource}", delta);
        Assert.DoesNotContain("[automation]", delta);       // already seen
        Assert.DoesNotContain("LIVE timeline turned", delta);
    }

    [Fact]
    public void Delta_with_the_timeline_off_reports_no_event_and_no_recipe()
    {
        var earlier = Timeline().Take(1).ToList();
        var later = Timeline().Append(Event(20, "event:automation", "Nightly build passed", -1)).ToList();

        var delta = Delta(
            Snapshot(LiveTimelineContext.Select(later, on: false), on: false),
            Snapshot(LiveTimelineContext.Select(earlier, on: false), on: false));

        Assert.Contains("No context changes since last message.", delta);
        Assert.DoesNotContain("Nightly build", delta);
        Assert.DoesNotContain("Revolut", delta);
        Assert.DoesNotContain("Read the LIVE timeline", delta);
    }

    [Fact]
    public void A_stored_snapshot_from_before_the_switch_is_not_a_switch()
    {
        // last_context_json as stored before the switch existed: a window, no liveTimeline.
        var stored = NovaContextBuilder.DeserializeSnapshot(NovaContextBuilder.SerializeSnapshot(
            NovaContextBuilder.BuildSnapshot([ChatEntry()], null, null, null,
                liveEvents: LiveTimelineContext.Select(Timeline(), on: true))))!;
        Assert.Null(stored.LiveTimeline);

        var delta = Delta(Snapshot([], on: false), stored);

        Assert.Contains("No context changes since last message.", delta);   // and no "turned off" line
    }

    [Fact]
    public void Turning_it_on_says_so_and_lists_only_what_arrived_since_the_last_turn()
    {
        var lastTurn = Now.AddMinutes(-9);
        var previous = Snapshot(LiveTimelineContext.Select(Timeline(), on: false), on: false, takenAt: lastTurn);
        var since = Timeline().Append(Event(20, "event:mail", "A new mail from the landlord", -2)).ToList();

        var delta = Delta(Snapshot(LiveTimelineContext.Select(since, on: true), on: true), previous);

        Assert.Contains("- LIVE timeline turned on here", delta);
        Assert.Contains("- [mail] A new mail from the landlord", delta);
        // The old window is not news: none of it is replayed as new.
        foreach (var old in EveryEvent) Assert.DoesNotContain(old, delta);
    }

    [Fact]
    public void Turning_it_off_says_so_and_no_event_appears()
    {
        var lastTurn = Now.AddMinutes(-9);
        var previous = Snapshot(LiveTimelineContext.Select(Timeline(), on: true), on: true, takenAt: lastTurn);
        var since = Timeline()
            .Append(Event(20, "event:mail", "Another Revolut mail", -3))
            .Append(Event(21, "event:automation", "Nightly build passed", -2)).ToList();

        var delta = Delta(Snapshot(LiveTimelineContext.Select(since, on: false), on: false), previous);

        Assert.Contains("- LIVE timeline turned off here", delta);
        Assert.DoesNotContain("Nightly build", delta);
        Assert.DoesNotContain("Revolut", delta);
    }

    [Fact]
    public void The_golden_contexts_are_the_ones_from_before_the_switch()
    {
        Assert.Equal(NovaContextGolden.Full, NovaContextFrameTests.Full(NovaContextFrameTests.Representative()));
        Assert.Equal(NovaContextGolden.Delta, NovaContextFrameTests.Delta(NovaContextFrameTests.Representative()));
    }

    // ── Through MessagePipeline ────────────────────────────────────

    [Theory]
    [InlineData("chat", null, false)]
    [InlineData("chat", true, true)]                        // a life-admin discussion sees its mail
    [InlineData("live", null, true)]
    [InlineData("live", false, false)]
    [InlineData(HeartbeatService.DiscussionType, null, true)]
    [InlineData(HeartbeatService.DiscussionType, false, false)]
    public async Task The_pipeline_sends_the_timeline_by_the_effective_setting(string type, bool? chosen, bool on)
    {
        using var w = new ReactionContextTests.World(type, liveTimeline: chosen);
        // In LIVE the timeline is the discussion itself.
        var live = type == "live" ? w.Store.DiscussionId : w.Store.AddDiscussion("live0001", "live");
        foreach (var record in Timeline().Where(m => m.Role != "user"))
            await w.Stream.PostAsync(live, "system", record.Content, (JsonObject)record.Metadata.DeepClone());

        await w.SendAsync("first");
        await w.Stream.PostAsync(live, "system", "Second Revolut mail", new JsonObject { ["source"] = "event:mail" });
        await w.Stream.PostAsync(live, "system", "Nightly build passed", new JsonObject { ["source"] = "event:automation" });
        await w.SendAsync("second");

        var full = w.Gateway.Sent[0].Content;
        var delta = w.Gateway.Sent[1].Content;
        Assert.DoesNotContain("scope=", OpenTag(full));
        Assert.DoesNotContain("scope=", OpenTag(delta));
        Assert.Equal(on, full.Contains("Recent LIVE events:"));
        foreach (var line in EveryEvent) Assert.Equal(on, full.Contains(line));
        Assert.Equal(!on, full.Contains(Recipe));
        Assert.Equal(on, delta.Contains("[mail] Second Revolut mail"));
        Assert.Equal(on, delta.Contains("[automation] Nightly build passed"));
        Assert.DoesNotContain("Read the LIVE timeline", delta);

        // What the next delta compares against holds exactly that.
        var stored = NovaContextBuilder.DeserializeSnapshot(w.Discussion().LastContextJson)!;
        Assert.Equal(on, stored.LiveTimeline);
        Assert.NotNull(stored.TakenAt);
        Assert.Equal(on, w.Discussion().LastContextJson!.Contains("Revolut"));
    }

    [Fact]
    public async Task Toggling_between_turns_announces_once_and_replays_nothing()
    {
        using var w = new ReactionContextTests.World("chat");
        var live = w.Store.AddDiscussion("live0001", "live");
        foreach (var record in Timeline().Where(m => m.Role != "user"))
            await w.Stream.PostAsync(live, "system", record.Content, (JsonObject)record.Metadata.DeepClone());
        var store = new DiscussionStore(w.Store, w.Stream);

        await w.SendAsync("off first");
        await store.SetLiveTimelineAsync(w.Store.DiscussionId, true);
        await w.Stream.PostAsync(live, "system", "Landlord replied about the lease", new JsonObject { ["source"] = "event:mail" });
        await w.SendAsync("now on");
        await w.SendAsync("still on");
        await store.SetLiveTimelineAsync(w.Store.DiscussionId, false);
        await w.Stream.PostAsync(live, "system", "Revolut again", new JsonObject { ["source"] = "event:mail" });
        await w.SendAsync("off again");

        var on = w.Gateway.Sent[1].Content;
        Assert.Contains("- LIVE timeline turned on here", on);
        Assert.Contains("[mail] Landlord replied about the lease", on);    // arrived after the last turn
        foreach (var old in EveryEvent) Assert.DoesNotContain(old, on);    // the old window is not news
        Assert.DoesNotContain("LIVE timeline turned", w.Gateway.Sent[2].Content);
        var off = w.Gateway.Sent[3].Content;
        Assert.Contains("- LIVE timeline turned off here", off);
        Assert.DoesNotContain("Revolut", off);
        Assert.DoesNotContain("Landlord", off);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static string OpenTag(string context) => context[..context.IndexOf('>')];

    internal static DiscussionMessage Event(long id, string source, string content, int minutes)
        => new(id, Guid.Empty, "system", content, new JsonObject { ["source"] = source },
            new DateTimeOffset(Now.AddMinutes(minutes)));

    private static DiscussionRead ChatEntry()
        => new("c0ffee01", "CC3 assets", null, "idle", Now.AddDays(-1), Now.AddMinutes(-1), 4, null, null, Guid.Empty, null);

    private static DiscussionRead Discussion(string type, bool? chosen)
        => ChatEntry() with { Type = type, LiveTimelineSetting = chosen };

    private static LeafEntity Entity(string type, JsonObject extra)
    {
        var data = new JsonObject { ["discussion_id"] = "d1", ["type"] = type };
        foreach (var (key, value) in extra) data[key] = value?.DeepClone();
        return new(Guid.NewGuid(), "discussion", "d1", "d1", data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
    }

    private static ContextSnapshot Snapshot(List<ContextSnapshot.LiveEventEntry> events, bool on, DateTime? takenAt = null)
        => NovaContextBuilder.BuildSnapshot([ChatEntry()], null, null, null, liveEvents: events,
            liveTimeline: on, takenAt: takenAt ?? Now);

    private static string Full(ContextSnapshot snapshot)
        => NovaContextBuilder.BuildFullContext(snapshot, "c0ffee01", Now, NovaContextFrameTests.Device(), "text", "Nova", []);

    private static string Delta(ContextSnapshot current, ContextSnapshot previous)
        => NovaContextBuilder.BuildDeltaContext(current, previous, "c0ffee01", Now, NovaContextFrameTests.Device(), "text", "Nova", []);
}
