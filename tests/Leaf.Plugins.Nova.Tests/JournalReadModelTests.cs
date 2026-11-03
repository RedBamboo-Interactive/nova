using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Whole days replayed through the read model over synthetic records: a fake transcript service that
/// pages newest first like the real one, a fake overlay, and fake peers.
/// </summary>
public sealed class JournalReadModelTests
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";
    private const string Colleague = "user-colleague";

    // 12:00 in Zurich on 2026-06-10; the journal day runs 2026-06-10T02:00Z to 2026-06-11T02:00Z.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-10T10:00:00Z");
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-06-10T06:00:00Z");

    // ── Which discussions are on the day ────────────────────────────────────────

    [Fact]
    public async Task A_record_at_0359_is_yesterday_and_at_0400_is_today()
    {
        var world = new World();
        var late = world.Chat("aaaaaaa1", "Late", DateTimeOffset.Parse("2026-06-09T20:00:00Z"), DateTimeOffset.Parse("2026-06-10T01:59:00Z"));
        world.Say(late, "user", "before the turn", "u1", DateTimeOffset.Parse("2026-06-09T20:00:00Z"));
        world.Say(late, "assistant", "the small hours", "u2", DateTimeOffset.Parse("2026-06-10T01:59:00Z"));
        var early = world.Chat("aaaaaaa2", "Early", DateTimeOffset.Parse("2026-06-10T02:00:00Z"), DateTimeOffset.Parse("2026-06-10T02:00:00Z"));
        world.Say(early, "user", "exactly four", "u3", DateTimeOffset.Parse("2026-06-10T02:00:00Z"));

        var today = await world.DayAsync();
        var yesterday = await world.DayAsync("2026-06-09");

        Assert.Equal(["thread:aaaaaaa2"], today.Entries.Select(entry => entry.Id));
        Assert.Equal(["thread:aaaaaaa1"], yesterday.Entries.Select(entry => entry.Id));
        Assert.Equal("the small hours", yesterday.Entries[0].Text);
        Assert.Equal("2026-06-09", today.PreviousDay);
        Assert.Null(today.NextDay);
        Assert.Equal("2026-06-10", yesterday.NextDay);
    }

    [Fact]
    public async Task A_discussion_spanning_two_days_appears_on_each_with_that_days_words()
    {
        var world = new World();
        var spanning = world.Chat("aaaaaaa1", "Spanning", Morning.AddDays(-1), Morning);
        world.Say(spanning, "user", "start it", "u1", Morning.AddDays(-1));
        world.Say(spanning, "assistant", "yesterday's words", "u2", Morning.AddDays(-1).AddMinutes(1));
        world.Say(spanning, "user", "carry on", "u3", Morning);
        world.Say(spanning, "assistant", "today's words", "u4", Morning.AddMinutes(1));

        var today = Assert.Single((await world.DayAsync()).Entries);
        var yesterday = Assert.Single((await world.DayAsync("2026-06-09")).Entries);

        Assert.Equal(Morning, today.At);
        Assert.Equal("today's words", today.Text);
        Assert.Equal(Morning.AddDays(-1), yesterday.At);
        Assert.Equal("yesterday's words", yesterday.Text);
        Assert.Equal(JournalCauses.WithYou, today.Cause);
        Assert.Equal(JournalCauses.WithYou, yesterday.Cause);
    }

    [Fact]
    public async Task A_candidate_with_no_visible_record_in_the_window_is_no_entry()
    {
        var world = new World();
        var bumped = world.Chat("aaaaaaa1", "Only touched", Morning.AddDays(-2), Morning);
        world.Say(bumped, "user", "long ago", "u1", Morning.AddDays(-2));
        world.Say(bumped, "assistant", NovaNoReply.Marker, "u2", Morning);

        Assert.Empty((await world.DayAsync()).Entries);
    }

    [Fact]
    public async Task An_archived_discussion_keeps_its_day_and_says_so()
    {
        var world = new World();
        var archived = world.Chat("aaaaaaa1", "Closed", Morning, Morning, status: "archived");
        world.Say(archived, "user", "hello", "u1", Morning);

        Assert.Equal("archived", Assert.Single((await world.DayAsync()).Entries).Status);
    }

    [Fact]
    public async Task The_chosen_text_skips_the_silent_settle()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Settled", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);
        world.Say(chat, "assistant", "The real answer.", "u2", Morning.AddMinutes(1));
        world.Say(chat, "user", "<nova-event source=\"x\">a late callback</nova-event>", "u3", Morning.AddMinutes(2));
        world.Say(chat, "assistant", NovaNoReply.Marker, "u4", Morning.AddMinutes(3));

        var entry = Assert.Single((await world.DayAsync()).Entries);

        Assert.Equal("The real answer.", entry.Text);
        Assert.Equal("u2", entry.TextMessageUid);
    }

    // ── The true first record ───────────────────────────────────────────────────

    [Fact]
    public async Task A_first_record_older_than_the_first_page_decides_and_a_callback_cannot_pretend()
    {
        var world = new World { PageSize = 2 };
        var chat = world.Chat("aaaaaaa1", "Long thread", Morning.AddDays(-30), Morning);
        world.Say(chat, "user", "The owner opened this.", "u1", Morning.AddDays(-30));
        for (var i = 0; i < 6; i++) world.Say(chat, "assistant", $"middle {i}", $"m{i}", Morning.AddDays(-29 + i));
        // The newest page opens with a callback, which looks exactly like an unstamped first event.
        world.Say(chat, "user", "<nova-event source=\"nova-1a2b3c4d\">a callback</nova-event>", "u2", Morning);
        world.Say(chat, "assistant", "Today's words.", "u3", Morning.AddMinutes(1));

        var entry = Assert.Single((await world.DayAsync()).Entries);

        Assert.Equal(JournalCauses.WithYou, entry.Cause);
        Assert.False(entry.Inferred);
        Assert.True(world.Gateway.TranscriptReads("session-aaaaaaa1") >= 4, "must have paged to the start");
    }

    [Fact]
    public async Task A_settled_cause_is_never_read_twice()
    {
        var world = new World { PageSize = 2 };
        var chat = world.Chat("aaaaaaa1", "Thread", Morning.AddDays(-5), Morning);
        world.Say(chat, "user", "first", "u1", Morning.AddDays(-5));
        for (var i = 0; i < 4; i++) world.Say(chat, "assistant", $"m{i}", $"m{i}", Morning.AddDays(-4 + i));
        world.Say(chat, "assistant", "more yesterday", "m9", Morning.AddHours(-20));
        world.Say(chat, "assistant", "today", "u9", Morning);

        await world.DayAsync();
        var walked = world.Gateway.TranscriptReads("session-aaaaaaa1");
        world.Time.Advance(TimeSpan.FromMinutes(1));
        chat["last_activity"] = Morning.AddMinutes(1).ToString("O"); // a new version, so the day is read again
        world.Say(chat, "assistant", "more today", "u10", Morning.AddMinutes(1));

        await world.DayAsync();

        Assert.True(walked >= 4, "the first read walked to the start");
        Assert.True(world.Gateway.TranscriptReads("session-aaaaaaa1") - walked <= 2, "a day read stops at the window");
    }

    [Fact]
    public async Task A_stamped_opening_with_a_request_quotes_what_was_asked_and_links_to_it()
    {
        var world = new World();
        var live = world.Live("bbbbbbb1", Morning, Morning);
        world.Say(live, "user", "Could you look at the fuse box?\nSecond line stays out.", "ask-1", Morning.AddMinutes(-1));
        var chat = world.Chat("aaaaaaa1", "Fuse box", Morning, Morning.AddMinutes(1));
        world.Note(chat, new EventSender(World.AgentId.ToString(), "bbbbbbb1", "Live", new RequestReference("bbbbbbb1", "ask-1")), "brief", "brief-1", Morning);
        world.Say(chat, "assistant", "On it.", "u1", Morning.AddMinutes(1));

        var day = await world.DayAsync();
        var entry = day.Entries.Single(item => item.Kind == JournalKinds.Thread);

        Assert.Equal(JournalCauses.Asked, entry.Cause);
        Assert.Equal("bbbbbbb1", entry.Request!.DiscussionId);
        Assert.Equal("ask-1", entry.Request.MessageUid);
        Assert.Equal("Could you look at the fuse box?", entry.Request.Quote);
        Assert.Contains(JournalFilters.WithYou, entry.Filters);
        Assert.Equal("bbbbbbb1", entry.StartedFrom!.DiscussionId);
    }

    [Fact]
    public async Task An_Agents_unstamped_first_words_read_as_inferred_own_and_an_automations_delivery_as_scheduled()
    {
        var world = new World();
        var own = world.Chat("aaaaaaa1", "Spoke first", Morning, Morning);
        world.Overlay(own, "assistant", "I started this.", new JsonObject { ["source"] = "nova-message", ["uid"] = "o1" }, Morning);
        var delivery = world.Chat("aaaaaaa2", "Delivery", Morning, Morning);
        delivery["automation_attempt_job_id"] = "attempt-1";
        world.Overlay(delivery, "assistant", "Your report.", new JsonObject { ["source"] = "nova-message", ["uid"] = "o2" }, Morning);

        var day = await world.DayAsync();

        var first = day.Entries.Single(entry => entry.DiscussionId == "aaaaaaa1");
        Assert.Equal(JournalCauses.Own, first.Cause);
        Assert.True(first.Inferred);
        var second = day.Entries.Single(entry => entry.DiscussionId == "aaaaaaa2");
        Assert.Equal(JournalCauses.Scheduled, second.Cause);
        Assert.False(second.Inferred);
    }

    [Fact]
    public async Task A_caller_refused_the_transcript_doesnt_settle_the_cause_for_everyone()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Thread", Morning.AddDays(-2), Morning, owner: "local-user"); // readable by both
        world.Say(chat, "user", "The owner opened this.", "u1", Morning.AddDays(-2));
        world.Overlay(chat, "assistant", "A later note.", new JsonObject { ["source"] = "nova-message", ["uid"] = "o1" }, Morning);
        world.Gateway.RefusedTranscripts.Add(Colleague);

        var refused = Assert.Single((await world.DayAsync(caller: Colleague)).Entries);
        var owner = Assert.Single((await world.DayAsync()).Entries);

        Assert.Equal(JournalCauses.Own, refused.Cause);
        Assert.Equal(JournalCauses.WithYou, owner.Cause);
    }

    // ── Ticks and edges ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_tick_is_an_entry_and_one_that_wrote_to_Live_counts_as_own()
    {
        var world = new World();
        var live = world.Live("bbbbbbb1", Morning, Morning.AddHours(1));
        var beat = world.Heartbeat("ccccccc1", Morning, Morning.AddHours(1));
        world.Say(beat, "user", "<heartbeat-tick/>", "t0", Morning);
        world.Say(beat, "assistant", NovaNoReply.Marker, "a0", Morning.AddSeconds(5));
        world.Say(beat, "user", "<heartbeat-tick/>", "t1", Morning.AddHours(1));
        world.Say(beat, "assistant", "Someone should look at this. More follows.", "a1", Morning.AddHours(1).AddSeconds(5));
        world.Note(live, new EventSender(World.AgentId.ToString(), "ccccccc1", "Heartbeat"), "a note", "note-1", Morning.AddHours(1).AddSeconds(3));

        var day = await world.DayAsync();
        var ticks = day.Entries.Where(entry => entry.Kind == JournalKinds.Tick).ToList();

        Assert.Equal(2, ticks.Count);
        Assert.True(ticks[0].Silent);
        Assert.Equal("Stayed silent", ticks[0].Title);
        Assert.DoesNotContain(JournalFilters.Own, ticks[0].Filters);
        Assert.Equal("Someone should look at this.", ticks[1].Title);
        Assert.Equal("Someone should look at this. More follows.", ticks[1].Text);
        Assert.Contains(JournalFilters.Own, ticks[1].Filters);
        Assert.Contains(JournalFilters.Ticks, ticks[1].Filters);
        Assert.Equal(JournalCauses.Tick, ticks[1].Cause);
        var sent = ticks[1].Links.Single(link => link.Kind == "note-sent");
        Assert.Equal("bbbbbbb1", sent.DiscussionId);
        Assert.Equal("note-1", sent.MessageUid);
        Assert.Equal(2, day.Counts.Ticks);
        Assert.Equal(1, day.Counts.Own);
    }

    [Fact]
    public async Task An_older_unstamped_note_gives_the_same_edge_marked_inferred()
    {
        var world = new World();
        var from = world.Chat("aaaaaaa1", "Sender", Morning, Morning);
        world.Say(from, "user", "hello", "u1", Morning);
        var to = world.Chat("aaaaaaa2", "Receiver", Morning, Morning.AddMinutes(5));
        world.Overlay(to, "user", "note", new JsonObject { ["source"] = "event:nova-aaaaaaa1", ["uid"] = "n1" }, Morning.AddMinutes(5));

        var day = await world.DayAsync();
        var sender = day.Entries.Single(entry => entry.DiscussionId == "aaaaaaa1");
        var receiver = day.Entries.Single(entry => entry.DiscussionId == "aaaaaaa2");

        var link = sender.Links.Single(item => item.Kind == "note-sent");
        Assert.Equal("aaaaaaa2", link.DiscussionId);
        Assert.Equal("n1", link.MessageUid);
        Assert.True(link.Inferred);
        Assert.Equal(JournalCauses.Own, receiver.Cause);
        Assert.True(receiver.Inferred);
        Assert.True(receiver.StartedFrom!.Inferred);
    }

    // ── Who may see what ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_confidential_discussion_is_gone_from_entries_counts_and_edges()
    {
        var world = new World();
        var open = world.Chat("aaaaaaa1", "Open", Morning, Morning);
        world.Say(open, "user", "hello", "u1", Morning);
        var secret = world.Chat("ddddddd1", "Secret plans", Morning, Morning, confidential: true);
        world.Say(secret, "user", "the secret words", "u2", Morning);
        // The secret thread wrote to the open one; a note from the open one reached the secret one.
        world.Note(open, new EventSender(World.AgentId.ToString(), "ddddddd1", "Secret plans"), "from secret", "n1", Morning.AddMinutes(1));
        world.Note(secret, new EventSender(World.AgentId.ToString(), "aaaaaaa1", "Open"), "to secret", "n2", Morning.AddMinutes(2));

        var day = await world.DayAsync();
        var json = JsonSerializer.Serialize(day, JournalJson.Options);

        var entry = Assert.Single(day.Entries);
        Assert.Equal("thread:aaaaaaa1", entry.Id);
        Assert.Equal(1, day.Counts.All);
        Assert.DoesNotContain(entry.Links, link => link.Kind == "note-sent");
        Assert.Null(entry.StartedFrom);
        Assert.DoesNotContain("ddddddd1", json);
        Assert.DoesNotContain("Secret plans", json);
        Assert.DoesNotContain("the secret words", json);
        Assert.DoesNotContain("session-ddddddd1", world.Gateway.Paths.Where(path => path.Contains("transcript")).Select(path => path.Split('/')[3]));
    }

    [Fact]
    public async Task A_confidential_job_is_no_entry_no_failure_and_no_count()
    {
        var world = new World();
        var open = world.Chat("aaaaaaa1", "Open", Morning, Morning);
        world.Say(open, "user", "hello", "u1", Morning);
        world.Peers.Automations = [new("11111111-1111-1111-1111-111111111111", "Hidden run", World.AgentId.ToString())];
        world.Job("Workflow: hidden", "workflow", "Failed", Morning, confidential: true, automation: "11111111-1111-1111-1111-111111111111");
        world.Job("Hidden failure", "ai-session", "Failed", Morning, confidential: true, discussion: "aaaaaaa1");
        world.Job("Sentinel: hidden", "workflow", "Completed", Morning, confidential: true);

        var day = await world.DayAsync();

        var entry = Assert.Single(day.Entries);
        Assert.Equal(0, entry.FailedJobs);
        Assert.Equal(0, day.Counts.Failed);
        Assert.Empty(day.Summary.Background);
        Assert.DoesNotContain("hidden", JsonSerializer.Serialize(day, JournalJson.Options), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_person_who_may_read_nothing_of_the_Agent_sees_an_empty_day_and_no_jobs()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Mine", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);
        world.Job("Sentinel: something", "workflow", "Completed", Morning);

        var day = await world.DayAsync(caller: Stranger);

        Assert.Empty(day.Entries);
        Assert.Null(day.Header);
        Assert.Empty(day.Summary.Background);
        Assert.DoesNotContain(world.Gateway.Paths, path => path.StartsWith("/jobs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_callers_cached_day_is_not_anothers()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Mine", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);

        Assert.Single((await world.DayAsync()).Entries);
        Assert.Empty((await world.DayAsync(caller: Stranger)).Entries);
    }

    // ── Commits ─────────────────────────────────────────────────────────────────

    private const string RealHash = "abc1234deadbeef00112233445566778899aabb";

    private static World WithCommitThread(string text)
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Builds", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);
        world.Say(chat, "assistant", text, "u2", Morning.AddMinutes(1));
        world.Job("Delegated", "ai-session", "Completed", Morning, discussion: "aaaaaaa1", repository: "widgets-12345678");
        world.Peers.Repositories = [
            new("r1", "widgets-12345678", "widgets", "https://user:s3cret@example.test/acme/widgets.git"),
            new("r2", "gadgets-12345678", "gadgets", null),
        ];
        world.Peers.Commits["widgets-12345678"] = [
            new(RealHash, "abc1234", "A real commit"),
            new("9999999999999999999999999999999999999999", "9999999", "Same day, never mentioned"),
        ];
        world.Peers.Commits["gadgets-12345678"] = [new("fedcba9876543210fedcba9876543210fedcba98", "fedcba9", "A commit elsewhere")];
        return world;
    }

    [Fact]
    public async Task A_commit_is_attributed_only_on_a_literal_hash_that_resolves()
    {
        var world = WithCommitThread("Landed abc1234. Also 0000fff is nothing we know.");

        var commit = Assert.Single(Assert.Single((await world.DayAsync()).Entries).Commits);

        Assert.Equal(RealHash, commit.Hash);
        Assert.Equal("widgets", commit.Repository);
        Assert.Equal($"https://example.test/acme/widgets/commit/{RealHash}", commit.Url);
        Assert.DoesNotContain("s3cret", commit.Url);
        Assert.Equal(["widgets-12345678"], world.Peers.CommitReads);
    }

    [Fact]
    public async Task No_hash_in_the_text_means_no_commit_and_no_read_whatever_the_time_window_holds()
    {
        var world = WithCommitThread("I committed the work this morning.");

        Assert.Empty(Assert.Single((await world.DayAsync()).Entries).Commits);
        Assert.Empty(world.Peers.CommitReads);
    }

    [Fact]
    public async Task A_repository_is_read_only_when_touched_or_named_and_a_remoteless_commit_has_no_url()
    {
        var unnamed = WithCommitThread("That was fedcba9.");
        Assert.Empty(Assert.Single((await unnamed.DayAsync()).Entries).Commits);
        Assert.Equal(["widgets-12345678"], unnamed.Peers.CommitReads);

        var named = WithCommitThread("In gadgets that was fedcba9.");
        var commit = Assert.Single(Assert.Single((await named.DayAsync()).Entries).Commits);
        Assert.Equal("gadgets", commit.Repository);
        Assert.Null(commit.Url);
    }

    // ── Automations and failures ────────────────────────────────────────────────

    private const string Mine = "11111111-1111-1111-1111-111111111111";
    private const string Theirs = "22222222-2222-2222-2222-222222222222";
    private const string Infrastructure = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task Only_the_Agents_own_automations_are_entries_and_the_outcome_is_the_sessions_words()
    {
        var world = new World();
        world.Chat("aaaaaaa1", "Keeps the Agent readable", Morning, Morning);
        world.Peers.Automations = [
            new(Mine, "system:Weekly review", World.AgentId.ToString()),
            new(Theirs, "Someone else's", Guid.NewGuid().ToString()),
            new(Infrastructure, "Database backup", null),
            new(World.HeartbeatAutomationId.ToString(), "Heartbeat", World.AgentId.ToString()),
        ];
        var run = world.Job("Workflow: review", "workflow", "Completed", Morning, automation: Mine);
        world.Job("Review session", "ai-session", "Completed", Morning.AddMinutes(1), parent: run, resultText: """{"success":true,"summary":"Reviewed the week."}""");
        world.Job("Workflow: other", "workflow", "Completed", Morning, automation: Theirs);
        world.Job("Workflow: backup", "workflow", "Completed", Morning, automation: Infrastructure);
        world.Job("Workflow: beat", "workflow", "Completed", Morning, automation: World.HeartbeatAutomationId.ToString());

        var day = await world.DayAsync();

        var entry = Assert.Single(day.Entries);
        Assert.Equal(JournalKinds.Automation, entry.Kind);
        Assert.Equal("Weekly review", entry.Title);
        Assert.Equal("Reviewed the week.", entry.Text);
        Assert.Equal(Mine, entry.AutomationId);
        Assert.Contains(entry.Links, link => link.Kind == "job" && link.JobId == run.ToString());
    }

    [Fact]
    public async Task A_failed_session_fails_its_automation_and_a_failed_job_is_only_counted_on_its_thread()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Thread", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);
        world.Job("Delegated", "ai-session", "TimedOut", Morning, discussion: "aaaaaaa1");
        world.Job("Generate discussion title", "ai-session", "Failed", Morning, discussion: "aaaaaaa1");
        world.Peers.Automations = [new(Mine, "Nightly", World.AgentId.ToString())];
        var run = world.Job("Workflow: nightly", "workflow", "Completed", Morning, automation: Mine);
        world.Job("Nightly session", "ai-session", "Failed", Morning, parent: run, error: "It broke.");

        var day = await world.DayAsync();
        var thread = day.Entries.Single(entry => entry.Kind == JournalKinds.Thread);
        var automation = day.Entries.Single(entry => entry.Kind == JournalKinds.Automation);

        Assert.Equal("idle", thread.Status);
        Assert.Equal(1, thread.FailedJobs); // a background title job is not the thread failing
        Assert.Single(thread.Jobs);
        Assert.Contains(JournalFilters.Failed, thread.Filters);
        Assert.Contains(JournalFilters.WithYou, thread.Filters);
        Assert.Equal("failed", automation.Status);
        Assert.Equal("It broke.", automation.Text);
        Assert.Equal(2, day.Counts.Failed);
    }

    [Fact]
    public async Task The_footer_counts_jobs_no_entry_represents_and_the_counts_come_from_the_entries()
    {
        var world = new World();
        var live = world.Live("bbbbbbb1", Morning, Morning);
        world.Say(live, "user", "hello", "u1", Morning);
        world.Say(live, "assistant", "hi", "u2", Morning);
        world.Job(JournalProjection.DiscordOutboundJob, "workflow", "Completed", Morning);
        world.Job(JournalProjection.DiscordOutboundJob, "workflow", "Completed", Morning);
        world.Job("Sentinel: reader", "workflow", "Completed", Morning);
        world.Job("Generate discussion title for x", "ai-session", "Completed", Morning);
        world.Job("Pixoo clock", "workflow", "Completed", Morning);
        world.Job("Something unnamed", "workflow", "Completed", Morning);
        world.Job("Another Agent's", "workflow", "Completed", Morning, agent: Guid.NewGuid().ToString());

        var day = await world.DayAsync();

        Assert.Equal("bbbbbbb1", day.Summary.Live!.DiscussionId);
        Assert.Equal(2, day.Summary.Live.Messages);
        Assert.Equal(2, day.Summary.DiscordOutgoing);
        Assert.Equal(
            ["Sentinel:3", "Titles:1", "Pixoo:1", "Other:1"],
            day.Summary.Background.Select(group => $"{group.Group}:{group.Count}"));
    }

    // ── What a card shows of what it produced ───────────────────────────────────

    [Fact]
    public async Task A_cards_artifacts_are_what_the_Agent_produced_and_not_the_persons_attachments_or_addresses()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Made things", Morning, Morning);
        world.Say(chat, "user", "hello", "u1", Morning);
        var at = Morning.AddMinutes(1).ToString("O");
        world.Gateway.Artifacts["session-aaaaaaa1"] = $$$"""
            {"items":[
              {"key":"a1","kind":"image","origin":"assistant-text","role":"assistant","target":"/api/assets/made.png","first":{"messageUid":"m1","timestamp":"{{{at}}}"}},
              {"key":"a2","kind":"video","origin":"assistant-text","role":"assistant","target":"/api/assets/made.mp4","first":{"messageUid":"m1","timestamp":"{{{at}}}"}},
              {"key":"a3","kind":"audio","origin":"assistant-text","role":"assistant","target":"/api/assets/made.mp3","first":{"messageUid":"m1","timestamp":"{{{at}}}"}},
              {"key":"a4","kind":"document","origin":"assistant-text","role":"assistant","target":"/api/assets/made.pdf","first":{"messageUid":"m1","timestamp":"{{{at}}}"}},
              {"key":"u1","kind":"image","origin":"user-attachment","role":"user","target":"/ai-session/input-attachments/theirs.png","first":{"messageUid":"u1","timestamp":"{{{at}}}"}},
              {"key":"u2","kind":"document","origin":"user-attachment","role":"user","target":"/ai-session/input-attachments/theirs.pdf","first":{"messageUid":"u1","timestamp":"{{{at}}}"}},
              {"key":"l1","kind":"link","origin":"assistant-text","role":"assistant","target":"http://127.0.0.1:5000/?token=s3cret","first":{"messageUid":"m1","timestamp":"{{{at}}}"}}
            ],"page":{}}
            """;

        var day = await world.DayAsync();
        var entry = Assert.Single(day.Entries);

        Assert.Equal(["a1", "a2", "a3", "a4"], entry.Artifacts.Select(artifact => artifact.Key).Order());
        Assert.Equal("a1", entry.Artifacts[0].Key); // images first
        Assert.DoesNotContain("s3cret", JsonSerializer.Serialize(day, JournalJson.Options));
    }

    // ── Reads made with the caller's identity stay the caller's ─────────────────

    [Fact]
    public async Task Each_caller_gets_their_own_read_of_the_neighbours()
    {
        var world = WithCommitThread("Landed abc1234.");
        var theirs = world.Chat("bbbbbbb2", "Theirs", Morning, Morning, owner: Colleague);
        world.Say(theirs, "user", "hi", "t1", Morning);
        world.Say(theirs, "assistant", "Landed abc1234.", "t2", Morning.AddMinutes(1));
        world.Job("Delegated", "ai-session", "Completed", Morning, discussion: "bbbbbbb2", repository: "widgets-12345678");

        await world.DayAsync();
        await world.DayAsync(caller: Colleague);

        Assert.Equal(1, world.Peers.ReadsBy(Owner, "automations"));
        Assert.Equal(1, world.Peers.ReadsBy(Colleague, "automations"));
        Assert.Equal(1, world.Peers.ReadsBy(Owner, "repositories"));
        Assert.Equal(1, world.Peers.ReadsBy(Colleague, "repositories"));
        Assert.Equal(1, world.Peers.ReadsBy(Owner, "commits"));
        Assert.Equal(1, world.Peers.ReadsBy(Colleague, "commits"));

        world.Time.Advance(TimeSpan.FromSeconds(6));
        await world.DayAsync();
        Assert.Equal(1, world.Peers.ReadsBy(Owner, "automations")); // still kept, for that caller
    }

    [Fact]
    public async Task A_non_owner_gets_no_outcome_text_they_couldnt_read_themselves_even_after_the_owner_read_it()
    {
        var world = new World();
        world.Chat("aaaaaaa1", "Keeps the Agent readable", Morning, Morning);
        var theirs = world.Chat("bbbbbbb2", "Theirs", Morning, Morning, owner: Colleague);
        world.Say(theirs, "user", "hi", "t1", Morning);
        world.Peers.Automations = [new(Mine, "Nightly", World.AgentId.ToString())];
        var run = world.Job("Workflow: nightly", "workflow", "Completed", Morning, automation: Mine);
        var session = world.Job("Nightly session", "ai-session", "Completed", Morning, parent: run, resultText: "The private outcome.");
        world.Gateway.RefusedJobs[Colleague] = [session];

        var owner = await world.DayAsync();
        var colleague = await world.DayAsync(caller: Colleague);

        Assert.Equal("The private outcome.", owner.Entries.Single(entry => entry.Kind == JournalKinds.Automation).Text);
        Assert.Null(colleague.Entries.Single(entry => entry.Kind == JournalKinds.Automation).Text);
        Assert.DoesNotContain("The private outcome.", JsonSerializer.Serialize(colleague, JournalJson.Options));
    }

    [Fact]
    public async Task The_outfits_reason_and_the_mood_are_the_install_owners_alone()
    {
        var world = new World();
        world.Chat("aaaaaaa1", "Owner's", Morning, Morning);
        world.Chat("bbbbbbb2", "Theirs", Morning, Morning, owner: Colleague);
        world.Outfit("Today's coat", "/api/assets/coat.png", Morning, "A private reason");
        using var workspace = world.Workspace("Updated: 2026-06-10 03:31 UTC\nEnergy: Bright\nVibe: Curious\n");

        var owner = await world.DayAsync();
        var colleague = await world.DayAsync(caller: Colleague);

        Assert.Equal("A private reason", owner.Header!.Outfit!.Reason);
        Assert.Equal("Bright", owner.Header.Mood!.Energy);
        Assert.Equal("Today's coat", colleague.Header!.Outfit!.Name);
        Assert.Equal("/api/assets/coat.png", colleague.Header.Outfit.AssetUrl);
        Assert.Null(colleague.Header.Outfit.Reason);
        Assert.Null(colleague.Header.Mood);
        var json = JsonSerializer.Serialize(colleague, JournalJson.Options);
        Assert.DoesNotContain("A private reason", json);
        Assert.DoesNotContain("Curious", json);
    }

    [Fact]
    public async Task Whole_days_are_kept_under_their_own_small_bound()
    {
        var world = new World();

        for (var back = 0; back < 70; back++)
            await world.DayAsync(new DateOnly(2026, 6, 10).AddDays(-back).ToString("yyyy-MM-dd"));

        Assert.InRange(world.CachedResponses, 1, 64);
    }

    // ── Soft dependencies ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_neighbour_that_isnt_installed_is_an_omitted_field_and_not_a_partial_day()
    {
        var world = WithCommitThread("Landed abc1234.");
        world.Peers.Automations = null;
        world.Peers.Repositories = null;

        var day = await world.DayAsync();

        Assert.False(day.Partial);
        Assert.Empty(Assert.Single(day.Entries).Commits);
    }

    [Theory]
    [InlineData("automations")]
    [InlineData("repositories")]
    public async Task A_neighbour_failing_is_a_partial_day_that_says_which(string source)
    {
        var world = WithCommitThread("Landed abc1234.");
        world.Peers.Failing = source;

        var day = await world.DayAsync();

        Assert.True(day.Partial);
        Assert.Contains(source, day.Unavailable);
        Assert.Single(day.Entries);
    }

    [Fact]
    public async Task A_RedCompute_that_ignores_the_range_is_a_short_read_and_never_a_wrong_day()
    {
        var world = new World();
        world.Chat("aaaaaaa1", "Keeps the Agent readable", Morning, Morning);
        world.Gateway.IgnoresRange = true;
        world.Peers.Automations = [new(Mine, "Nightly", World.AgentId.ToString())];
        world.Job("Workflow: nightly", "workflow", "Failed", Morning.AddDays(-3), automation: Mine);

        var day = await world.DayAsync();

        Assert.True(day.Partial);
        Assert.Contains("jobs", day.Unavailable);
        Assert.Empty(day.Entries);
    }

    [Fact]
    public async Task A_transcript_service_that_is_down_is_partial_and_the_overlay_still_speaks()
    {
        var world = new World();
        var chat = world.Chat("aaaaaaa1", "Thread", Morning, Morning);
        world.Overlay(chat, "user", "hello", new JsonObject { ["source"] = "user-message", ["uid"] = "u1" }, Morning);
        world.Gateway.TranscriptStatus = HttpStatusCode.ServiceUnavailable;

        var day = await world.DayAsync();

        Assert.True(day.Partial);
        Assert.Contains("transcripts", day.Unavailable);
    }

    // ── The header ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_days_outfit_and_a_mood_dated_that_day_head_it_and_a_past_day_has_no_mood()
    {
        var world = new World();
        world.Chat("aaaaaaa1", "Keeps the Agent readable", Morning.AddDays(-1), Morning);
        world.Outfit("Today's coat", "/api/assets/coat.png", Morning, "Rain today");
        world.Outfit("Yesterday's coat", "/api/assets/old.png", Morning.AddDays(-1), null);
        using var workspace = world.Workspace("Updated: 2026-06-10 03:31 UTC\nEnergy: Bright\nVibe: Curious\n");

        var today = await world.DayAsync();
        var yesterday = await world.DayAsync("2026-06-09");

        Assert.Equal("Today's coat", today.Header!.Outfit!.Name);
        Assert.Equal("/api/assets/coat.png", today.Header.Outfit.AssetUrl);
        Assert.Equal("Rain today", today.Header.Outfit.Reason);
        Assert.Equal("Bright", today.Header.Mood!.Energy);
        Assert.Equal("Yesterday's coat", yesterday.Header!.Outfit!.Name);
        Assert.Null(yesterday.Header.Mood);
    }

    // ── Refusals ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("nonsense", 400, "invalid_day")]
    [InlineData("2026-13-40", 400, "invalid_day")]
    [InlineData("2026-06-11", 400, "future_day")]
    public async Task A_day_that_cant_be_read_is_refused(string day, int status, string code)
    {
        var refusal = await Assert.ThrowsAsync<JournalRequestException>(() => new World().DayAsync(day));

        Assert.Equal(status, refusal.Status);
        Assert.Equal(code, refusal.Code);
    }

    [Fact]
    public async Task An_unknown_Agent_is_not_found()
    {
        var refusal = await Assert.ThrowsAsync<JournalRequestException>(() => new World().DayAsync(agent: Guid.NewGuid().ToString()));

        Assert.Equal(404, refusal.Status);
    }

    // ── The calendar ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_calendar_marks_each_day_from_the_list_and_jobs_without_reading_a_transcript()
    {
        var world = new World { Time = new(DateTimeOffset.Parse("2026-06-20T10:00:00Z")) };
        var day3 = DateTimeOffset.Parse("2026-06-03T10:00:00Z");
        var day5 = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        var day9 = DateTimeOffset.Parse("2026-06-09T10:00:00Z");
        var day11 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
        var day13 = DateTimeOffset.Parse("2026-06-13T10:00:00Z");
        var withYou = world.Chat("aaaaaaa1", "Together", day3, day3);
        world.Overlay(withYou, "user", "hello", new JsonObject { ["source"] = "user-message" }, day3);
        var own = world.Chat("aaaaaaa2", "Alone", day5, day5);
        world.Note(own, new EventSender(World.AgentId.ToString(), "bbbbbbb1", "Live"), "brief", "b1", day5);
        world.Job("Delegated", "ai-session", "Failed", day5, discussion: "aaaaaaa1");
        world.Peers.Automations = [new(Mine, "Nightly", World.AgentId.ToString())];
        world.Job("Workflow: nightly", "workflow", "TimedOut", day9, automation: Mine);
        var completed = world.Job("Workflow: nightly", "workflow", "Completed", day11, automation: Mine);
        world.Job("Nightly session", "ai-session", "Failed", day11, parent: completed);
        world.Job("Workflow: clean", "workflow", "Completed", day13, automation: Mine);
        world.Job("Workflow: beat", "workflow", "Failed", day13, automation: World.HeartbeatAutomationId.ToString());
        var secret = world.Chat("ddddddd1", "Secret", DateTimeOffset.Parse("2026-06-07T10:00:00Z"), DateTimeOffset.Parse("2026-06-07T10:00:00Z"), confidential: true);
        world.Overlay(secret, "user", "hush", new JsonObject { ["source"] = "user-message" }, DateTimeOffset.Parse("2026-06-07T10:00:00Z"));

        var month = await world.MonthAsync("2026-06");

        Assert.Equal(0, world.Gateway.TranscriptReads());
        Assert.Equal(["2026-06-03", "2026-06-05", "2026-06-09", "2026-06-11", "2026-06-13"], month.Days.Select(day => day.Day));
        Assert.True(month.Days[0].WithYou);
        Assert.False(month.Days[0].Own);
        Assert.False(month.Days[0].Failed);
        Assert.True(month.Days[1].Own);
        Assert.True(month.Days[1].WithYou); // the failed job placed the first thread here too
        Assert.False(month.Days[1].Failed); // and a thread's failed job is no failed mark
        Assert.True(month.Days[2].Failed); // the run itself failed
        Assert.True(month.Days[3].Failed); // the run completed with a failed session under it
        Assert.False(month.Days[4].Failed); // Heartbeat's automation is the ticks' business
    }

    // ── The world ───────────────────────────────────────────────────────────────

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class World
    {
        public static readonly Guid AgentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        public static readonly Guid HeartbeatAutomationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        public FixedTime Time { get; init; } = new(Now);
        public int PageSize { get => Gateway.PageSize; init => Gateway.PageSize = value; }
        public Entities Store { get; } = new();
        public Overlays Messages { get; } = new();
        public Gateway Gateway { get; } = new();
        public Peers Peers { get; } = new();
        public Workspaces Resolver { get; } = new();

        public int CachedResponses => Model.CachedResponseCount;

        private JournalReadModel? _model;
        private JournalReadModel Model => _model ??= new JournalReadModel(
            new DiscussionStore(Store, Messages), Messages, new RedComputeClient(Gateway), Store, Resolver, Peers,
            () => AgentId.ToString(), Time);

        public World()
        {
            Store.Add(HeartbeatAutomationId, "automation", "heartbeat", "Heartbeat",
                new JsonObject { ["trigger"] = new JsonObject { ["timezone"] = "Europe/Zurich" } }, Now);
            Store.Add(AgentId, "agent", "test-agent", "Test Agent",
                new JsonObject { ["heartbeat_automation"] = HeartbeatAutomationId.ToString() }, Now);
        }

        private JsonObject Discussion(string id, string type, string title, DateTimeOffset created, DateTimeOffset last, string status, bool confidential, string owner = Owner)
        {
            var data = new JsonObject
            {
                ["discussion_id"] = id, ["app"] = "nova", ["type"] = type, ["status"] = status, ["title"] = title,
                ["owner_id"] = owner, ["agent"] = AgentId.ToString(), ["confidential"] = confidential,
                ["session_id"] = $"session-{id}", ["created_at"] = created.ToString("O"), ["last_activity"] = last.ToString("O"),
            };
            data["_entity"] = Store.Add(Guid.NewGuid(), "discussion", DiscussionStore.Slug(id), title, data, created).ToString();
            return data;
        }

        public JsonObject Chat(string id, string title, DateTimeOffset created, DateTimeOffset last, string status = "idle", bool confidential = false, string owner = Owner)
            => Discussion(id, "chat", title, created, last, status, confidential, owner);

        public JsonObject Live(string id, DateTimeOffset created, DateTimeOffset last)
            => Discussion(id, "live", "Live", created, last, "idle", false);

        public JsonObject Heartbeat(string id, DateTimeOffset created, DateTimeOffset last)
            => Discussion(id, HeartbeatService.DiscussionType, "Heartbeat", created, last, "idle", false);

        public void Say(JsonObject discussion, string role, string content, string uid, DateTimeOffset at)
            => Gateway.Add(discussion["session_id"]!.GetValue<string>(), new SessionMessage
            {
                Id = Gateway.NextId(), Role = role, EventType = "text", Content = content, MessageUid = uid, Timestamp = at.UtcDateTime,
            });

        public void Overlay(JsonObject discussion, string role, string content, JsonObject metadata, DateTimeOffset at)
            => Messages.Add(Guid.Parse(discussion["_entity"]!.GetValue<string>()), role, content, metadata, at);

        public void Note(JsonObject discussion, EventSender sender, string content, string uid, DateTimeOffset at)
        {
            var metadata = new JsonObject { ["source"] = "event:agent-note", ["uid"] = uid };
            sender.WriteTo(metadata);
            Overlay(discussion, "user", content, metadata, at);
        }

        public void Outfit(string name, string asset, DateTimeOffset at, string? reason)
        {
            var data = new JsonObject { ["agent"] = AgentId.ToString(), ["asset"] = asset };
            if (reason is not null) data["reasoning"] = reason;
            Store.Add(Guid.NewGuid(), "outfit", Guid.NewGuid().ToString("N"), name, data, at);
        }

        public IDisposable Workspace(string mood)
        {
            var folder = Path.Combine(Path.GetTempPath(), $"journal-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(folder, "memory", "dreaming"));
            File.WriteAllText(Path.Combine(folder, "memory", "dreaming", "Mood.md"), mood);
            Resolver.Root = folder;
            return new Deleting(folder);
        }

        private sealed class Deleting(string folder) : IDisposable
        {
            public void Dispose() => Directory.Delete(folder, recursive: true);
        }

        public Guid Job(string name, string capability, string status, DateTimeOffset queuedAt, bool confidential = false,
            string? automation = null, string? discussion = null, string? repository = null, Guid? parent = null,
            string? resultText = null, string? error = null, string? agent = null)
        {
            var id = Guid.NewGuid();
            var context = new JsonArray();
            if (automation is not null) context.Add(new JsonObject { ["kind"] = "automation", ["id"] = automation });
            if (discussion is not null) context.Add(new JsonObject { ["kind"] = "discussion", ["id"] = discussion });
            if (repository is not null) context.Add(new JsonObject { ["kind"] = "repository", ["id"] = repository });
            var provenance = new JsonObject { ["context"] = context };
            if (agent is not null) provenance["actor"] = new JsonObject { ["kind"] = "agent", ["entityId"] = agent };
            var item = new JsonObject
            {
                ["id"] = id.ToString(), ["name"] = name, ["capability"] = capability, ["status"] = status,
                ["queuedAt"] = queuedAt.ToString("O"), ["confidential"] = confidential, ["creationProvenance"] = provenance,
            };
            if (parent is not null) item["parentJobId"] = parent.ToString();
            if (error is not null) item["errorMessage"] = error;
            Gateway.Jobs.Add(item);
            if (resultText is not null) Gateway.JobResults[id] = resultText;
            return id;
        }

        // Only the install's owner carries the role; everyone else is just someone with a discussion.
        private DefaultHttpContext Caller(string subject)
        {
            Gateway.Caller = subject;
            Claim[] claims = subject == Owner
                ? [new Claim("sub", subject), new Claim("roles", "admin")]
                : [new Claim("sub", subject)];
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
            Peers.Caller = subject;
            return context;
        }

        public Task<JournalDayResponse> DayAsync(string? day = null, string caller = Owner, string? agent = null)
            => Model.ReadDayAsync(agent, day, Caller(caller));

        public Task<JournalMonthResponse> MonthAsync(string month, string caller = Owner)
            => Model.ReadMonthAsync(null, month, Caller(caller));
    }

    private sealed class Gateway : IComputeGateway
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, List<SessionMessage>> _transcripts = new(StringComparer.Ordinal);
        private long _id;

        public int PageSize { get; set; } = 500;
        public bool IgnoresRange { get; set; }
        public HttpStatusCode? TranscriptStatus { get; set; }
        public string? Caller { get; set; }
        public HashSet<string> RefusedTranscripts { get; } = [];
        public Dictionary<string, List<Guid>> RefusedJobs { get; } = [];
        public Dictionary<string, string> Artifacts { get; } = [];
        public List<JsonObject> Jobs { get; } = [];
        public Dictionary<Guid, string> JobResults { get; } = [];
        public List<string> Paths { get; } = [];

        public long NextId() => ++_id;

        public void Add(string sessionId, SessionMessage message)
        {
            lock (_transcripts)
            {
                if (!_transcripts.TryGetValue(sessionId, out var list)) _transcripts[sessionId] = list = [];
                list.Add(message);
            }
        }

        public int TranscriptReads(string? sessionId = null)
        {
            lock (Paths)
                return Paths.Count(path => path.Contains("/transcript-page") && (sessionId is null || path.Contains($"/{sessionId}/")));
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var url = request.RequestUri!.OriginalString;
            lock (Paths) Paths.Add(url);
            var path = url.Split('?')[0];
            var query = url.Contains('?')
                ? url[(url.IndexOf('?') + 1)..].Split('&').Select(part => part.Split('=', 2))
                    .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]))
                : [];

            if (path.EndsWith("/transcript-page", StringComparison.Ordinal))
            {
                if (TranscriptStatus is { } status) return Answer(status, "{}");
                if (Caller is not null && RefusedTranscripts.Contains(Caller)) return Answer(HttpStatusCode.Forbidden, "{}");
                return Answer(HttpStatusCode.OK, Transcript(path.Split('/')[3], query.GetValueOrDefault("before")));
            }
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Answer(HttpStatusCode.OK, Artifacts.GetValueOrDefault(path.Split('/')[3]) ?? """{"items":[],"page":{}}""");
            if (path == "/jobs") return Answer(HttpStatusCode.OK, ListJobs(query));
            if (path.StartsWith("/jobs/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path["/jobs/".Length..]);
                if (Caller is not null && RefusedJobs.TryGetValue(Caller, out var refused) && refused.Contains(id))
                    return Answer(HttpStatusCode.Forbidden, "{}");
                return Answer(HttpStatusCode.OK, new JsonObject
                {
                    ["id"] = id.ToString(),
                    ["result"] = JobResults.TryGetValue(id, out var text) ? new JsonObject { ["text"] = text } : null,
                }.ToJsonString());
            }
            return Answer(HttpStatusCode.NotFound, "{}");
        }

        private string Transcript(string sessionId, string? before)
        {
            List<SessionMessage> all;
            lock (_transcripts) all = _transcripts.GetValueOrDefault(sessionId) ?? [];
            var end = before is null ? all.Count : int.Parse(before);
            var start = Math.Max(0, end - PageSize);
            return JsonSerializer.Serialize(new SessionTranscriptPage
            {
                Session = new SessionPageInfo { Id = sessionId },
                Messages = all.GetRange(start, end - start),
                Page = new SessionTranscriptPageMetadata { Epoch = "e1", OldestCursor = start.ToString(), HasEarlier = start > 0, BoundaryComplete = true },
            }, Json);
        }

        private string ListJobs(IReadOnlyDictionary<string, string> query)
        {
            IEnumerable<JsonObject> rows = Jobs;
            if (!IgnoresRange)
            {
                var since = DateTimeOffset.Parse(query["since"]);
                var until = DateTimeOffset.Parse(query["until"]);
                rows = rows.Where(job =>
                {
                    var at = DateTimeOffset.Parse(job["queuedAt"]!.GetValue<string>());
                    return at >= since && at < until;
                });
            }
            if (query.TryGetValue("status", out var wanted))
                rows = rows.Where(job => job["status"]!.GetValue<string>() == wanted);
            if (query.TryGetValue("capability", out var capability))
                rows = rows.Where(job => job["capability"]!.GetValue<string>() == capability);
            if (query.TryGetValue("contextKind", out var kind))
                rows = rows.Where(job => job["creationProvenance"]!["context"]!.AsArray()
                    .Any(context => context!["kind"]!.GetValue<string>() == kind));
            var offset = query.TryGetValue("offset", out var skip) ? int.Parse(skip) : 0;
            var items = new JsonArray(rows.Skip(offset).Select(job => (JsonNode)job.DeepClone()).ToArray());
            return new JsonObject { ["items"] = items }.ToJsonString();
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string content)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class Peers : IJournalPeers
    {
        public IReadOnlyList<JournalAutomationInfo>? Automations { get; set; } = [];
        public IReadOnlyList<JournalRepositoryInfo>? Repositories { get; set; } = [];
        public Dictionary<string, IReadOnlyList<JournalCommitInfo>> Commits { get; } = new(StringComparer.Ordinal);
        public List<string> CommitReads { get; } = [];
        public string? Failing { get; set; }
        public string? Caller { get; set; }
        private readonly List<string> _reads = [];

        public int ReadsBy(string caller, string source)
        {
            lock (_reads) return _reads.Count(read => read == $"{caller}|{source}");
        }

        private T Answer<T>(string source, T value)
        {
            lock (_reads) _reads.Add($"{Caller}|{source}");
            return Failing == source ? throw new JournalSourceUnavailableException(source, "down") : value;
        }

        public Task<IReadOnlyList<JournalAutomationInfo>?> AutomationsAsync(HttpContext caller, CancellationToken ct)
            => Task.FromResult(Answer("automations", Automations));

        public Task<IReadOnlyList<JournalRepositoryInfo>?> RepositoriesAsync(HttpContext caller, CancellationToken ct)
            => Task.FromResult(Answer("repositories", Repositories));

        public Task<IReadOnlyList<JournalCommitInfo>?> CommitsAsync(HttpContext caller, string repository, DateTimeOffset since, CancellationToken ct)
        {
            lock (CommitReads) CommitReads.Add(repository);
            lock (_reads) _reads.Add($"{Caller}|commits");
            return Task.FromResult<IReadOnlyList<JournalCommitInfo>?>(Commits.GetValueOrDefault(repository) ?? []);
        }
    }

    private sealed class Workspaces : IAgentWorkspacePathResolver
    {
        public string? Root { get; set; }
        public Task<string?> ResolveAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult(Root);
    }

    private sealed class Entities : IEntityStore
    {
        private readonly List<LeafEntity> _items = [];

        public Guid Add(Guid id, string type, string slug, string name, JsonObject data, DateTimeOffset created)
        {
            _items.Add(new LeafEntity(id, type, slug, name, data, created, created, "test"));
            return id;
        }

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(_items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default)
            => Task.FromResult(_items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            var rows = _items.Where(entity => entity.TypeSlug == query.TypeSlug);
            if (query.DataEquals is { } equals)
                rows = rows.Where(entity => equals.All(pair => entity.Data[pair.Key]?.ToString() == pair.Value?.ToString()));
            return Task.FromResult<IReadOnlyList<LeafEntity>>(rows.ToList());
        }

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Overlays : IDiscussions
    {
        private readonly List<DiscussionMessage> _records = [];

        public void Add(Guid discussion, string role, string content, JsonObject metadata, DateTimeOffset at)
            => _records.Add(new(_records.Count + 1, discussion, role, content, metadata, at));

        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscussionMessage>>(_records
                .Where(record => record.DiscussionId == discussionId && record.Id > afterId).Take(limit).ToList());

        public Task<DiscussionMessagePage> GetMessagePageAsync(Guid discussionId, DiscussionMessagePageRequest request, CancellationToken ct = default)
        {
            var rows = _records.Where(record => record.DiscussionId == discussionId
                && (request.FromInclusive is null || record.CreatedAt >= request.FromInclusive.CreatedAt)
                && (request.ToExclusive is null || record.CreatedAt < request.ToExclusive.CreatedAt)).ToList();
            return Task.FromResult(new DiscussionMessagePage(rows, false, false, rows.FirstOrDefault()?.Id, rows.LastOrDefault()?.Id));
        }

        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
