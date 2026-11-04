using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>The Journal's rules as pure replays: the day, the cause, a turn's words, grouping, mood, remotes.</summary>
public sealed class JournalProjectionTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
    private static readonly DateTimeOffset T0 = new(2026, 6, 10, 8, 0, 0, TimeSpan.Zero);

    private static DiscussionMessage Overlay(string source, string role = "user", EventSender? sender = null, string? uid = null, DateTimeOffset? at = null, long id = 1)
    {
        var metadata = new JsonObject { ["source"] = source };
        if (uid is not null) metadata["uid"] = uid;
        sender?.WriteTo(metadata);
        return new(id, Guid.Empty, role, "words", metadata, at ?? T0);
    }

    private static SessionMessage Canonical(string role, string? content, string? uid = null, DateTimeOffset? at = null, string eventType = "text")
        => new() { Role = role, EventType = eventType, Content = content, MessageUid = uid, Timestamp = (at ?? T0).UtcDateTime };

    // ── A record's time ─────────────────────────────────────────────────────────

    [Fact]
    public void A_transcript_time_with_an_offset_keeps_its_real_instant()
    {
        // The shape RedCompute's transcript page returns, read the way the client reads it.
        var message = JsonSerializer.Deserialize<SessionMessage>(
            """{"role":"assistant","eventType":"text","content":"words","timestamp":"2026-11-03T06:55:12.5+01:00"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(DateTimeOffset.Parse("2026-11-03T05:55:12.5Z"), JournalProjection.At(message));
    }

    [Fact]
    public void An_unmarked_transcript_time_is_taken_as_utc()
    {
        var message = new SessionMessage { Timestamp = new DateTime(2026, 11, 3, 5, 55, 0, DateTimeKind.Unspecified) };

        Assert.Equal(DateTimeOffset.Parse("2026-11-03T05:55:00Z"), JournalProjection.At(message));
    }

    [Fact]
    public void A_record_without_a_time_falls_back_to_when_it_was_stored()
    {
        var stored = DateTimeOffset.Parse("2026-11-03T05:55:00Z");

        Assert.Equal(stored, JournalProjection.At(new SessionMessage { RecordCreatedAt = stored }));
    }

    // ── The day ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-06-10T01:59:00Z", "2026-06-09")] // 03:59 in summer time
    [InlineData("2026-06-10T02:00:00Z", "2026-06-10")] // 04:00 in summer time
    [InlineData("2026-01-10T02:59:00Z", "2026-01-09")] // 03:59 in winter time
    [InlineData("2026-01-10T03:00:00Z", "2026-01-10")] // 04:00 in winter time
    public void A_day_turns_over_at_four_in_the_morning(string instant, string day)
        => Assert.Equal(DateOnly.Parse(day), JournalWindow.DayOf(DateTimeOffset.Parse(instant), Zurich));

    [Fact]
    public void The_window_is_half_open_at_four_on_both_sides()
    {
        var window = JournalWindow.For(new DateOnly(2026, 6, 10), Zurich);

        Assert.Equal(DateTimeOffset.Parse("2026-06-10T02:00:00Z"), window.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-06-11T02:00:00Z"), window.End);
        Assert.True(window.Contains(window.Start));
        Assert.False(window.Contains(window.Start.AddSeconds(-1)));
        Assert.False(window.Contains(window.End));
    }

    [Theory]
    // Clocks go forward at 02:00 on 2026-03-29: that journal day has 23 hours.
    [InlineData("2026-03-28", "2026-03-28T03:00:00Z", "2026-03-29T02:00:00Z", 23)]
    // Clocks go back at 03:00 on 2026-10-25: that journal day has 25 hours.
    [InlineData("2026-10-24", "2026-10-24T02:00:00Z", "2026-10-25T03:00:00Z", 25)]
    public void A_clock_change_moves_the_length_of_the_day_and_not_its_edges(string day, string start, string end, int hours)
    {
        var window = JournalWindow.For(DateOnly.Parse(day), Zurich);

        Assert.Equal(DateTimeOffset.Parse(start), window.Start);
        Assert.Equal(DateTimeOffset.Parse(end), window.End);
        Assert.Equal(TimeSpan.FromHours(hours), window.End - window.Start);
    }

    // ── The cause rules, in order ───────────────────────────────────────────────

    private static readonly EventSender Agent = new("agent-1", "aaaaaaaa", "Origin");

    [Fact]
    public void Rule_1_a_stamped_event_with_a_request_reference_was_asked_for()
    {
        var reference = new RequestReference("bbbbbbbb", "uid-1");
        var cause = JournalProjection.DecideCause(
            JournalProjection.FromOverlay(Overlay("event:agent-note", sender: Agent with { RequestedIn = reference })), false);

        Assert.Equal(JournalCauses.Asked, cause.Cause);
        Assert.False(cause.Inferred);
        Assert.Equal(reference, cause.Request);
    }

    [Fact]
    public void Rule_2_a_stamped_event_without_one_is_the_Agents_own()
    {
        var cause = JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("event:agent-note", sender: Agent)), false);

        Assert.Equal(JournalCauses.Own, cause.Cause);
        Assert.False(cause.Inferred);
        Assert.Null(cause.Request);
    }

    [Theory]
    [InlineData("event:nova-instance")]
    [InlineData("event:nova-1a2b3c4d")]
    [InlineData("event:initiative")]
    [InlineData("event:agent-note")]
    public void Rule_3_an_older_unstamped_event_from_an_instance_is_inferred_own(string source)
    {
        var cause = JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay(source)), false);

        Assert.Equal(JournalCauses.Own, cause.Cause);
        Assert.True(cause.Inferred);
    }

    [Fact]
    public void Rule_4_plain_text_from_the_owner_is_with_you()
    {
        Assert.Equal(JournalCauses.WithYou, JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("user-message")), false).Cause);
        Assert.Equal(JournalCauses.WithYou, JournalProjection.DecideCause(JournalProjection.FromCanonical(Canonical("user", "hello")), false).Cause);
    }

    [Fact]
    public void Rule_5_a_discussion_started_by_an_automation_is_scheduled()
    {
        Assert.Equal(JournalCauses.Scheduled, JournalProjection.DecideCause(null, startedByAutomation: true).Cause);
        Assert.Equal(JournalCauses.Scheduled, JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("nova-message", "assistant")), true).Cause);
    }

    [Fact]
    public void An_unstamped_assistant_nova_message_first_is_inferred_own()
    {
        var cause = JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("nova-message", "assistant")), false);

        Assert.Equal(JournalCauses.Own, cause.Cause);
        Assert.True(cause.Inferred);
    }

    [Fact]
    public void An_automations_delivery_discussion_stays_scheduled_because_that_rule_comes_first()
    {
        var cause = JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("nova-message", "assistant")), startedByAutomation: true);

        Assert.Equal(JournalCauses.Scheduled, cause.Cause);
        Assert.False(cause.Inferred);
    }

    [Theory]
    [InlineData("event:something-unheard-of", "assistant")]
    [InlineData("event:some-reader", "user")]
    [InlineData("event:someone-handoff", "user")]
    [InlineData("nova-message", "user")]
    public void Rule_6_anything_else_is_not_recorded_and_is_never_guessed(string source, string role)
    {
        var cause = JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay(source, role)), false);

        Assert.Equal(JournalCauses.Unknown, cause.Cause);
        Assert.False(cause.Inferred);
    }

    [Fact]
    public void A_rule_higher_up_wins_over_being_started_by_an_automation()
        => Assert.Equal(JournalCauses.WithYou,
            JournalProjection.DecideCause(JournalProjection.FromOverlay(Overlay("user-message")), startedByAutomation: true).Cause);

    [Fact]
    public void A_framed_canonical_first_record_reads_as_its_event_and_not_as_the_owner()
    {
        var record = Canonical("user", """<nova-event source="nova-1a2b3c4d" channel="x">note</nova-event>""");

        var cause = JournalProjection.DecideCause(JournalProjection.FromCanonical(record), false);

        Assert.Equal(JournalCauses.Own, cause.Cause);
        Assert.True(cause.Inferred);
    }

    [Fact]
    public void The_earlier_stream_is_first_but_only_the_overlay_carries_the_stamp()
    {
        var stamped = Overlay("event:agent-note", sender: Agent, uid: "u1", at: T0.AddMinutes(1));
        var canonicalCopy = Canonical("user", "<nova-event source=\"agent-note\">x</nova-event>", "u1", T0);
        Assert.NotNull(JournalProjection.PickFirst([stamped], canonicalCopy)!.Stamped);

        // Nothing ties the two together: the transcript began first.
        var later = Overlay("event:agent-note", sender: Agent, uid: "u9", at: T0.AddHours(1));
        Assert.Equal("user-message", JournalProjection.PickFirst([later], Canonical("user", "hello", "u1", T0))!.Source);

        // System records never open a discussion.
        var system = Overlay("system-note", "system", at: T0.AddHours(-1));
        Assert.Equal("user-message", JournalProjection.PickFirst([system], Canonical("user", "hello", "u1", T0))!.Source);
    }

    // ── A turn's words ──────────────────────────────────────────────────────────

    [Fact]
    public void A_threads_last_words_skip_the_silent_settle()
    {
        var spoken = JournalProjection.LastSpoken([
            Canonical("assistant", "First thought.", "u1"),
            Canonical("assistant", "The last real words.", "u2"),
            Canonical("assistant", NovaNoReply.Marker, "u3"),
            Canonical("user", "a later message", "u4"),
        ]);

        Assert.Equal("The last real words.", spoken!.Content);
        Assert.Equal("u2", spoken.MessageUid);
    }

    [Fact]
    public void Real_words_before_a_trailing_marker_are_what_the_turn_said()
    {
        var turns = JournalProjection.Turns([
            Canonical("user", "<nova-event source=\"heartbeat-tick\">tick</nova-event>", "u0", T0),
            Canonical("assistant", "Waiting on the worker.\n\n" + NovaNoReply.Marker, "t1", T0.AddSeconds(5)),
        ]);

        Assert.False(Assert.Single(turns).Silent);
        Assert.Equal("Waiting on the worker.", turns[0].Text);
    }

    [Fact]
    public void A_turn_ending_in_the_marker_stayed_silent_whatever_it_said_on_the_way()
    {
        var turns = JournalProjection.Turns([
            Canonical("user", "<heartbeat-tick/>", "u0", T0),
            Canonical("assistant", "Thinking out loud.", "t1", T0.AddSeconds(5)),
            Canonical("assistant", NovaNoReply.Marker, "t1", T0.AddSeconds(9)),
            Canonical("user", "<nova-event source=\"heartbeat-tick\">tick</nova-event>", "u1", T0.AddHours(1)),
            Canonical("assistant", "He should see this.", "t2", T0.AddHours(1).AddSeconds(4)),
            Canonical("user", "<nova-event source=\"nova-1a2b3c4d\">note</nova-event>", "u2", T0.AddHours(2)),
            Canonical("assistant", "Read the note.", "t3", T0.AddHours(2).AddSeconds(4)),
        ]);

        Assert.Equal(3, turns.Count);
        Assert.True(turns[0].Silent);
        Assert.Null(turns[0].Text);
        Assert.Equal("tick", turns[0].Trigger);
        Assert.Equal(T0, turns[0].At);
        Assert.False(turns[1].Silent);
        Assert.Equal("He should see this.", turns[1].Text);
        Assert.Equal("tick", turns[1].Trigger);
        Assert.Equal("note", turns[2].Trigger);
    }

    [Theory]
    [InlineData("He asked twice. Then nothing.", "He asked twice.")]
    [InlineData("Version 1.2 shipped\nsecond line", "Version 1.2 shipped")]
    [InlineData("No stop at all", "No stop at all")]
    public void A_title_is_the_first_sentence_of_the_text_and_nothing_else(string text, string title)
        => Assert.Equal(title, JournalProjection.FirstSentence(text));

    [Fact]
    public void A_long_title_is_cut_not_rewritten()
    {
        var title = JournalProjection.FirstSentence(new string('a', 300));

        Assert.Equal(JournalProjection.TitleLength, title.Length);
        Assert.EndsWith("…", title);
    }

    // ── Hashes and remotes ──────────────────────────────────────────────────────

    [Fact]
    public void Hash_candidates_are_literal_hex_words_and_never_plain_numbers()
    {
        var found = JournalProjection.HashCandidates([
            "Landed abc1234 and 0123abcdef. The port 1880418 and the id xabc1234 and abc1234y don't count; ABCDEF1 isn't a hash either.",
            null,
            "abc1234 again",
        ]);

        Assert.Equal(["abc1234", "0123abcdef"], found);
    }

    [Theory]
    [InlineData("https://example.test/acme/widgets.git", "https://example.test/acme/widgets")]
    [InlineData("https://user:s3cret@example.test/acme/widgets/", "https://example.test/acme/widgets")]
    [InlineData("git@example.test:acme/widgets.git", "https://example.test/acme/widgets")]
    [InlineData("C:/Projects/widgets", null)]
    [InlineData("file:///srv/widgets.git", null)]
    [InlineData("https://example.test/", null)]
    [InlineData("", null)]
    public void A_remote_is_browsable_or_absent_and_never_carries_credentials(string remote, string? expected)
        => Assert.Equal(expected, JournalProjection.RemoteBase(remote));

    // ── Grouping ────────────────────────────────────────────────────────────────

    private static JournalEntry Automation(string id, DateTimeOffset at, string status = "completed", string? text = null) => new()
    {
        Id = id, Kind = JournalKinds.Automation, Cause = JournalCauses.Scheduled, At = at, Title = id, Status = status, Text = text,
        Filters = JournalProjection.FiltersOf(JournalKinds.Automation, JournalCauses.Scheduled, status == "failed" ? 1 : 0, false),
        Jobs = [new(id, id, "Completed", "workflow")],
    };

    private static JournalEntry Thread(string id, DateTimeOffset at) => new()
    {
        Id = id, Kind = JournalKinds.Thread, Cause = JournalCauses.WithYou, At = at, Title = id, Status = "idle",
        Filters = JournalProjection.FiltersOf(JournalKinds.Thread, JournalCauses.WithYou, 0, false),
    };

    private static readonly DateTimeOffset Night = DateTimeOffset.Parse("2026-06-10T02:30:00Z"); // 04:30 local
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-06-10T10:00:00Z"); // 12:00 local

    [Fact]
    public void Consecutive_automations_within_ninety_minutes_collapse_and_are_named_by_their_start()
    {
        var early = Assert.Single(JournalProjection.GroupAutomations([
            Automation("a", Night), Automation("b", Night.AddMinutes(90), text: "b's outcome."), Automation("c", Night.AddMinutes(180)),
        ]));
        Assert.Equal(JournalKinds.AutomationGroup, early.Kind);
        Assert.Equal("Scheduled", early.Title);
        Assert.Equal(3, early.Lines.Count);

        var day = Assert.Single(JournalProjection.GroupAutomations([Automation("a", Noon), Automation("b", Noon.AddMinutes(10))]));
        Assert.Equal("Scheduled", day.Title);
    }

    [Fact]
    public void A_group_has_no_words_of_its_own_and_each_line_says_its_runs_first_sentence()
    {
        var group = Assert.Single(JournalProjection.GroupAutomations([
            Automation("a", Night, text: "Slept well. Then much more that stays out."),
            Automation("b", Night.AddMinutes(10)),
            Automation("c", Night.AddMinutes(20), text: "Skipped, no sound.\nA second line."),
        ]));

        Assert.Null(group.Text);
        Assert.Equal(["Slept well.", null, "Skipped, no sound."], group.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Only_an_entry_between_them_ends_the_run()
    {
        var apart = JournalProjection.GroupAutomations([Automation("a", Noon), Automation("b", Noon.AddHours(3))]);
        Assert.Equal([JournalKinds.AutomationGroup], apart.Select(entry => entry.Kind));

        var between = JournalProjection.GroupAutomations([
            Automation("a", Noon), Thread("t", Noon.AddMinutes(1)), Automation("b", Noon.AddMinutes(2)),
        ]);
        Assert.Equal([JournalKinds.Automation, JournalKinds.Thread, JournalKinds.Automation], between.Select(entry => entry.Kind));
    }

    [Fact]
    public void A_failed_automation_is_never_grouped_away()
    {
        var entries = JournalProjection.GroupAutomations([
            Automation("a", Noon), Automation("b", Noon.AddMinutes(1)),
            Automation("broken", Noon.AddMinutes(2), "failed"),
            Automation("c", Noon.AddMinutes(3)), Automation("d", Noon.AddMinutes(4)),
        ]);

        Assert.Equal([JournalKinds.AutomationGroup, JournalKinds.Automation, JournalKinds.AutomationGroup], entries.Select(entry => entry.Kind));
        Assert.Equal(1, JournalProjection.Count(entries).Failed);
        Assert.Equal(3, JournalProjection.Count(entries).Scheduled);
    }

    // ── Filters and mood ────────────────────────────────────────────────────────

    [Fact]
    public void Failed_is_as_well_as_an_entrys_own_filter_and_a_tick_that_wrote_to_Live_counts_as_own()
    {
        Assert.Equal([JournalFilters.WithYou, JournalFilters.Failed], JournalProjection.FiltersOf(JournalKinds.Thread, JournalCauses.Asked, 22, false));
        Assert.Equal([JournalFilters.Ticks, JournalFilters.Own], JournalProjection.FiltersOf(JournalKinds.Tick, JournalCauses.Tick, 0, true));
        Assert.Equal([JournalFilters.Ticks], JournalProjection.FiltersOf(JournalKinds.Tick, JournalCauses.Tick, 0, false));
        Assert.Empty(JournalProjection.FiltersOf(JournalKinds.Thread, JournalCauses.Unknown, 0, false));
    }

    [Theory]
    [InlineData("idle", "idle")]
    [InlineData("thinking", "running")]
    [InlineData("stopped", "stopped")]
    [InlineData("archived", "archived")]
    public void A_threads_status_is_its_discussions(string discussion, string status)
        => Assert.Equal(status, JournalProjection.ThreadStatus(discussion));

    [Fact]
    public void Mood_shows_only_when_the_file_is_dated_that_journal_day()
    {
        const string content = "Updated: 2026-06-10 03:31 UTC\nEnergy: Bright\nVibe: Curious\n";

        var day = JournalProjection.ReadMood(content, JournalWindow.For(new DateOnly(2026, 6, 10), Zurich));
        Assert.Equal("Bright", day!.Energy);
        Assert.Equal("Curious", day.Vibe);

        Assert.Null(JournalProjection.ReadMood(content, JournalWindow.For(new DateOnly(2026, 6, 9), Zurich)));
        Assert.Null(JournalProjection.ReadMood("Energy: Bright\nVibe: Curious", JournalWindow.For(new DateOnly(2026, 6, 10), Zurich)));
        Assert.Null(JournalProjection.ReadMood(null, JournalWindow.For(new DateOnly(2026, 6, 10), Zurich)));
    }

    [Theory]
    [InlineData("""{"success":true,"summary":"Slept well."}""", "Slept well.")]
    [InlineData("""{"success":true}""", """{"success":true}""")]
    [InlineData("{not json", "{not json")]
    [InlineData("Plain words.", "Plain words.")]
    public void An_outcome_is_verbatim_and_unpacks_only_a_machine_shaped_summary(string text, string expected)
        => Assert.Equal(expected, JournalProjection.OutcomeText(text));
}
