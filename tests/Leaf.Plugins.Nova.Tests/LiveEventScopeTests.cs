using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Which LIVE events reach a discussion's <nova-context>. On 2026-11-10 a coding discussion
// (fca95f0f) was handed a Revolut "account restricted" mail notice and a Heartbeat note meant for
// LIVE, and the worker raised Laurent's bank account mid-task. A discussion with personal context
// sees the whole timeline; without it, only an allowlist of operational sources. Personal context is
// the discussion's own setting: on by default for LIVE and Heartbeat, off for the rest, and an
// explicit choice wins both ways (a person may open an ordinary discussion for life admin).
public sealed class LiveEventScopeTests
{
    private static readonly DateTime Now = NovaContextFrameTests.Now;

    private const string Mail = "New mail From: Revolut / Subject: Your account is restricted";
    private const string HeartbeatNote = "Revolut sent a second account restricted mail; check in ten minutes";
    private const string AgentNote = "Note from Gemma: Laurent seemed stressed this morning";
    private const string FutureSource = "A future extension says something personal";
    private const string Music = "Now playing in Kitchen: Holocene — Bon Iver";

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

    private static readonly string[] Allowed =
    [
        "[automation] Morning brief completed",
        "[callback] Code session 3f2a finished: tests green",
        "[discussion] Laurent in \"CC3 assets\": export the atlas",
        "[presence] Laurent is at the office",
        "[device] Switched to Desktop",
    ];

    private static readonly string[] Personal = [Mail, HeartbeatNote, AgentNote, FutureSource, Music];

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
        Assert.Equal(expected, LiveEventScope.DefaultFor(type));
        Assert.Equal(expected, (ChatEntry() with { Type = type! }).PersonalContext);
    }

    [Theory]
    [InlineData("chat", true)]                              // life admin in an ordinary discussion
    [InlineData("chat", false)]
    [InlineData("live", false)]                             // a LIVE that keeps personal events out
    [InlineData("live", true)]
    [InlineData(HeartbeatService.DiscussionType, false)]
    public void An_explicit_choice_wins_over_the_type_both_ways(string type, bool chosen)
    {
        var discussion = Discussion(type, chosen);

        Assert.Equal(chosen, discussion.PersonalContext);
        Assert.Equal(chosen, LiveEventScope.IsPersonal(discussion));
    }

    [Fact]
    public void The_choice_is_read_from_the_record_and_absent_means_default()
    {
        var chat = DiscussionStore.Map(Entity("chat", new JsonObject { [LiveEventScope.DataKey] = true }))!;
        var live = DiscussionStore.Map(Entity("live", new JsonObject { [LiveEventScope.DataKey] = false }))!;
        var unset = DiscussionStore.Map(Entity("live", new JsonObject()))!;
        var cleared = DiscussionStore.Map(Entity("chat", new JsonObject { [LiveEventScope.DataKey] = null }))!;

        Assert.Equal((true, true), (chat.PersonalContextSetting, chat.PersonalContext));
        Assert.Equal((false, false), (live.PersonalContextSetting, live.PersonalContext));
        Assert.Equal((null, true), (unset.PersonalContextSetting, unset.PersonalContext));
        Assert.Equal((null, false), (cleared.PersonalContextSetting, cleared.PersonalContext));
    }

    // ── What each scope sees ───────────────────────────────────────

    [Fact]
    public void Work_sees_only_operational_sources()
    {
        var events = LiveEventScope.Select(Timeline(), personal: false);

        Assert.Equal(
            ["event:automation", "event:callback", "event:discussion", "event:presence", "event:device"],
            events.Select(e => e.Source));
    }

    [Fact]
    public void Personal_keeps_every_event_exactly_as_before()
    {
        var timeline = Timeline();

        var events = LiveEventScope.Select(timeline, personal: true);

        // The selection MessagePipeline made before the scope existed.
        var before = timeline
            .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
            .TakeLast(15)
            .ToList();
        Assert.Equal(before.Select(m => m.Metadata["source"]!.GetValue<string>()), events.Select(e => e.Source));
        Assert.Equal(before.Select(m => m.Content), events.Select(e => e.Content));
        Assert.Equal(before.Select(m => m.CreatedAt.UtcDateTime), events.Select(e => e.Timestamp));
    }

    [Theory]
    [InlineData("event:Mail")]
    [InlineData("event:mail")]
    [InlineData("event:heartbeat")]
    [InlineData("event:agent-note")]
    [InlineData("event:automation-digest")]  // a prefix of an allowed name is not that name
    [InlineData("event: automation")]
    [InlineData("event:")]
    [InlineData("automation")]               // not an event record at all
    [InlineData("user-message")]
    [InlineData(null)]
    public void Anything_not_named_on_the_list_is_refused_for_work(string? source)
        => Assert.False(LiveEventScope.Admits(personal: false, source));

    [Theory]
    [InlineData("event:automation")]
    [InlineData("event:callback")]
    [InlineData("event:discussion")]
    [InlineData("event:presence")]
    [InlineData("event:device")]
    public void The_operational_sources_are_admitted_for_work(string source)
        => Assert.True(LiveEventScope.Admits(personal: false, source));

    [Theory]
    [InlineData("automation")]
    [InlineData("user-message")]
    [InlineData(null)]
    public void Personal_still_admits_only_events(string? source)
        => Assert.False(LiveEventScope.Admits(personal: true, source));

    [Fact]
    public void The_scope_applies_before_the_cut_of_fifteen()
    {
        // Twenty automation outcomes, then thirty mail notices on top of them.
        var timeline = Enumerable.Range(0, 20).Select(i => Event(i + 1, "event:automation", $"run {i}", -100 + i))
            .Concat(Enumerable.Range(0, 30).Select(i => Event(i + 21, "event:mail", $"mail {i}", -50 + i)))
            .ToList();

        var work = LiveEventScope.Select(timeline, personal: false);
        var personal = LiveEventScope.Select(timeline, personal: true);

        Assert.Equal(Enumerable.Range(5, 15).Select(i => $"run {i}"), work.Select(e => e.Content));
        Assert.Equal(Enumerable.Range(15, 15).Select(i => $"mail {i}"), personal.Select(e => e.Content));
    }

    [Fact]
    public void An_event_keeps_its_own_timestamp_when_it_carries_one()
    {
        var at = Now.AddMinutes(-42);
        var record = Event(1, "event:automation", "done", -1);
        record.Metadata["timestamp"] = new DateTimeOffset(at).ToString("O");

        Assert.Equal(at, LiveEventScope.Select([record], personal: false).Single().Timestamp);
    }

    // ── The scope the agent is told ────────────────────────────────

    [Theory]
    [InlineData(true, "personal")]
    [InlineData(false, "work")]
    public void Full_and_delta_tags_state_the_scope(bool personal, string scope)
    {
        var snapshot = Snapshot(LiveEventScope.Select(Timeline(), personal), personal);

        Assert.Contains($" scope=\"{scope}\"", OpenTag(Full(snapshot)));
        Assert.Contains($" scope=\"{scope}\"", OpenTag(Delta(snapshot, snapshot)));
        Assert.Contains($" scope=\"{scope}\"", OpenTag(NovaContextBuilder.BuildReactionNote(
            ["Laurent reacted 👍 to your latest message: \"ok\""], Now, NovaContextFrameTests.Device(), "c0ffee01", "Nova",
            steered: false, LiveEventScope.ScopeOf(personal))));
    }

    [Fact]
    public void A_snapshot_with_no_scope_is_told_work_never_personal()
    {
        var unscoped = Snapshot(LiveEventScope.Select(Timeline(), personal: false), personal: false);
        unscoped.Scope = null;

        Assert.Contains(" scope=\"work\"", OpenTag(Full(unscoped)));
        Assert.Contains(" scope=\"work\"", OpenTag(Delta(unscoped, unscoped)));
    }

    // ── Full context ───────────────────────────────────────────────

    [Fact]
    public void Full_context_of_a_work_discussion_lists_no_personal_event()
    {
        var full = Full(Snapshot(LiveEventScope.Select(Timeline(), personal: false), personal: false));

        Assert.Contains("Recent LIVE events:", full);
        foreach (var line in Allowed) Assert.Contains(line, full);
        foreach (var personal in Personal) Assert.DoesNotContain(personal, full);
        Assert.DoesNotContain("[mail]", full);
        Assert.DoesNotContain("[heartbeat]", full);
        Assert.DoesNotContain("Revolut", full);
    }

    [Fact]
    public void Full_context_with_personal_context_lists_every_event()
    {
        var full = Full(Snapshot(LiveEventScope.Select(Timeline(), personal: true), personal: true));

        foreach (var line in Allowed) Assert.Contains(line, full);
        Assert.Contains($"[mail] {Mail}", full);
        Assert.Contains($"[heartbeat] {HeartbeatNote}", full);
        Assert.Contains($"[agent-note] {AgentNote}", full);
        Assert.Contains($"[some-new-extension] {FutureSource}", full);
        Assert.Contains($"[sonos] {Music}", full);
    }

    [Fact]
    public void A_work_discussion_with_only_personal_traffic_has_no_live_section()
    {
        var timeline = new List<DiscussionMessage> { Event(1, "event:mail", Mail, -2), Event(2, "event:heartbeat", HeartbeatNote, -1) };

        var full = Full(Snapshot(LiveEventScope.Select(timeline, personal: false), personal: false));

        Assert.DoesNotContain("Recent LIVE events:", full);
        Assert.DoesNotContain("Revolut", full);
    }

    // ── Changes since last message ─────────────────────────────────

    [Fact]
    public void Delta_of_a_work_discussion_reports_only_new_operational_events()
    {
        var earlier = Timeline().Take(5).ToList();          // up to the discussion activity
        var later = Timeline();                             // everything since, personal included

        var delta = Delta(
            Snapshot(LiveEventScope.Select(later, personal: false), personal: false),
            Snapshot(LiveEventScope.Select(earlier, personal: false), personal: false));

        Assert.Contains("Changes since last message:", delta);
        Assert.Contains("- [presence] Laurent is at the office", delta);
        Assert.Contains("- [device] Switched to Desktop", delta);
        Assert.DoesNotContain("[automation]", delta);       // already seen
        foreach (var personal in Personal) Assert.DoesNotContain(personal, delta);
        Assert.DoesNotContain("Revolut", delta);
        Assert.DoesNotContain("Personal context turned", delta);
    }

    [Fact]
    public void Delta_of_a_work_discussion_with_only_new_personal_events_reports_no_change()
    {
        var earlier = Timeline().Take(1).ToList();
        var later = earlier.Append(Event(20, "event:mail", Mail, -1))
            .Append(Event(21, "event:heartbeat", HeartbeatNote, 0)).ToList();

        var delta = Delta(
            Snapshot(LiveEventScope.Select(later, personal: false), personal: false),
            Snapshot(LiveEventScope.Select(earlier, personal: false), personal: false));

        Assert.Contains("No context changes since last message.", delta);
        Assert.DoesNotContain("Revolut", delta);
    }

    [Fact]
    public void A_stored_unscoped_snapshot_never_makes_personal_events_reappear_as_new()
    {
        // The last_context_json a work discussion holds from before scopes existed: everything, no scope.
        var stored = NovaContextBuilder.DeserializeSnapshot(NovaContextBuilder.SerializeSnapshot(
            NovaContextBuilder.BuildSnapshot([ChatEntry()], null, null, null,
                liveEvents: LiveEventScope.Select(Timeline(), personal: true))))!;
        Assert.Null(stored.Scope);
        var later = Timeline().Append(Event(20, "event:mail", "Another mail from Revolut", -1)).ToList();

        var delta = Delta(Snapshot(LiveEventScope.Select(later, personal: false), personal: false), stored);

        Assert.Contains("No context changes since last message.", delta);   // and no "turned off" line
        foreach (var personal in Personal) Assert.DoesNotContain(personal, delta);
        Assert.DoesNotContain("Revolut", delta);
    }

    [Fact]
    public void Delta_with_personal_context_still_reports_new_personal_events()
    {
        var earlier = Timeline().Take(1).ToList();
        var later = earlier.Append(Event(20, "event:mail", Mail, -1)).ToList();

        var delta = Delta(
            Snapshot(LiveEventScope.Select(later, personal: true), personal: true),
            Snapshot(LiveEventScope.Select(earlier, personal: true), personal: true));

        Assert.Contains($"- [mail] {Mail}", delta);
    }

    [Fact]
    public void Turning_personal_on_says_so_and_lists_only_what_arrived_since_the_last_turn()
    {
        // The last turn was work, at -9 minutes; its window held the operational events only.
        var lastTurn = Now.AddMinutes(-9);
        var previous = Snapshot(LiveEventScope.Select(Timeline(), personal: false), personal: false, takenAt: lastTurn);
        var since = Timeline().Append(Event(20, "event:mail", "A new mail from the landlord", -2)).ToList();

        var delta = Delta(Snapshot(LiveEventScope.Select(since, personal: true), personal: true), previous);

        Assert.Contains($"- {NovaContextBuilder.PersonalContextOn}", delta);
        Assert.Contains("- [mail] A new mail from the landlord", delta);
        // The old personal window is not news: none of it is dumped as new.
        foreach (var old in Personal) Assert.DoesNotContain(old, delta);
        Assert.Contains(" scope=\"personal\"", OpenTag(delta));
    }

    [Fact]
    public void Turning_personal_off_says_so_and_nothing_personal_appears()
    {
        var lastTurn = Now.AddMinutes(-9);
        var previous = Snapshot(LiveEventScope.Select(Timeline(), personal: true), personal: true, takenAt: lastTurn);
        var since = Timeline()
            .Append(Event(20, "event:mail", "Another Revolut mail", -3))
            .Append(Event(21, "event:automation", "Nightly build passed", -2)).ToList();

        var delta = Delta(Snapshot(LiveEventScope.Select(since, personal: false), personal: false), previous);

        Assert.Contains($"- {NovaContextBuilder.PersonalContextOff}", delta);
        Assert.Contains("- [automation] Nightly build passed", delta);
        foreach (var personal in Personal) Assert.DoesNotContain(personal, delta);
        Assert.DoesNotContain("Revolut", delta);
        Assert.Contains(" scope=\"work\"", OpenTag(delta));
    }

    [Fact]
    public void The_golden_context_is_unchanged_apart_from_its_scope()
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
    public async Task The_pipeline_scopes_what_it_sends_by_the_effective_setting(string type, bool? chosen, bool personal)
    {
        using var w = new ReactionContextTests.World(type, personalContext: chosen);
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
        var scope = personal ? "personal" : "work";
        Assert.Contains($" scope=\"{scope}\"", OpenTag(full));
        Assert.Contains($" scope=\"{scope}\"", OpenTag(delta));
        Assert.Contains("[automation] Morning brief completed", full);
        Assert.Contains("[automation] Nightly build passed", delta);
        Assert.Equal(personal, full.Contains(Mail));
        Assert.Equal(personal, full.Contains(HeartbeatNote));
        Assert.Equal(personal, full.Contains(FutureSource));
        Assert.Equal(personal, delta.Contains("Second Revolut mail"));

        // What the next delta compares against holds nothing personal for a work discussion.
        var stored = NovaContextBuilder.DeserializeSnapshot(w.Discussion().LastContextJson)!;
        Assert.Equal(scope, stored.Scope);
        Assert.NotNull(stored.TakenAt);
        Assert.Equal(personal, w.Discussion().LastContextJson!.Contains("Revolut"));
    }

    [Fact]
    public async Task Toggling_between_turns_announces_once_and_floods_nothing()
    {
        using var w = new ReactionContextTests.World("chat");
        var live = w.Store.AddDiscussion("live0001", "live");
        foreach (var record in Timeline().Where(m => m.Role != "user"))
            await w.Stream.PostAsync(live, "system", record.Content, (JsonObject)record.Metadata.DeepClone());
        var store = new DiscussionStore(w.Store, w.Stream);

        await w.SendAsync("work first");
        await store.SetPersonalContextAsync(w.Store.DiscussionId, true);
        await w.Stream.PostAsync(live, "system", "Landlord replied about the lease", new JsonObject { ["source"] = "event:mail" });
        await w.SendAsync("now personal");
        await w.SendAsync("still personal");
        await store.SetPersonalContextAsync(w.Store.DiscussionId, false);
        await w.Stream.PostAsync(live, "system", "Revolut again", new JsonObject { ["source"] = "event:mail" });
        await w.SendAsync("back to work");

        var on = w.Gateway.Sent[1].Content;
        Assert.Contains(NovaContextBuilder.PersonalContextOn, on);
        Assert.Contains("[mail] Landlord replied about the lease", on);    // arrived after the last turn
        foreach (var old in Personal) Assert.DoesNotContain(old, on);      // the old window is not news
        Assert.DoesNotContain("Personal context turned", w.Gateway.Sent[2].Content);
        var off = w.Gateway.Sent[3].Content;
        Assert.Contains(NovaContextBuilder.PersonalContextOff, off);
        Assert.DoesNotContain("Revolut", off);
        Assert.DoesNotContain("Landlord", off);
        Assert.Contains(" scope=\"work\"", OpenTag(off));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static string OpenTag(string context) => context[..context.IndexOf('>')];

    private static DiscussionMessage Event(long id, string source, string content, int minutes)
        => new(id, Guid.Empty, "system", content, new JsonObject { ["source"] = source },
            new DateTimeOffset(Now.AddMinutes(minutes)));

    private static DiscussionRead ChatEntry()
        => new("c0ffee01", "CC3 assets", null, "idle", Now.AddDays(-1), Now.AddMinutes(-1), 4, null, null, Guid.Empty, null);

    private static DiscussionRead Discussion(string type, bool? chosen)
        => ChatEntry() with { Type = type, PersonalContextSetting = chosen };

    private static LeafEntity Entity(string type, JsonObject extra)
    {
        var data = new JsonObject { ["discussion_id"] = "d1", ["type"] = type };
        foreach (var (key, value) in extra) data[key] = value?.DeepClone();
        return new(Guid.NewGuid(), "discussion", "d1", "d1", data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
    }

    private static ContextSnapshot Snapshot(List<ContextSnapshot.LiveEventEntry> events, bool personal, DateTime? takenAt = null)
        => NovaContextBuilder.BuildSnapshot([ChatEntry()], null, null, null, liveEvents: events,
            scope: LiveEventScope.ScopeOf(personal), takenAt: takenAt ?? Now);

    private static string Full(ContextSnapshot snapshot)
        => NovaContextBuilder.BuildFullContext(snapshot, "c0ffee01", Now, NovaContextFrameTests.Device(), "text", "Nova", []);

    private static string Delta(ContextSnapshot current, ContextSnapshot previous)
        => NovaContextBuilder.BuildDeltaContext(current, previous, "c0ffee01", Now, NovaContextFrameTests.Device(), "text", "Nova", []);
}
