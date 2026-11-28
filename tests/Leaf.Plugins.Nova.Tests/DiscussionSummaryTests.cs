using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionSummaryTests
{
    private static readonly SummaryText Waiting = new("waiting", "Lab release", "The test is ready.", "Nova runs it after handoff.", null);

    [Fact]
    public async Task New_owners_default_on_and_a_saved_owner_opt_out_survives_restart()
    {
        using var h = new Harness();
        Assert.True(await h.Summaries.DefaultAsync(h.Owner, default));
        Assert.True(await h.NewService().DefaultAsync(Guid.NewGuid().ToString(), default));
        await h.Summaries.SetDefaultAsync(h.Owner, false, default);

        var restarted = h.NewService();
        Assert.False(await restarted.DefaultAsync(h.Owner, default));
        Assert.False(DiscussionSummaries.View(await h.ReadAsync(), false).Enabled);
        await restarted.SweepAsync([await h.ReadAsync()], default);
        Assert.Equal(0, h.Compute.Requests);

        var overrideOn = await restarted.SetOverrideAsync(await h.ReadAsync(), h.Person(), true);
        Assert.True(overrideOn?.Enabled);
        var inherited = await restarted.SetOverrideAsync(await h.ReadAsync(), h.Person(), null);
        Assert.False(inherited?.Enabled);
    }

    [Fact]
    public async Task A_discussion_opt_out_wins_over_the_shipped_default()
    {
        using var h = new Harness();
        var overrideOff = await h.Summaries.SetOverrideAsync(await h.ReadAsync(), h.Person(), false);
        Assert.False(overrideOff?.Enabled);
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Requests);

        var inherited = await h.Summaries.SetOverrideAsync(await h.ReadAsync(), h.Person(), null);
        Assert.True(inherited?.Enabled);
        Assert.Null(inherited?.Override);
    }

    [Fact]
    public async Task One_signed_fast_result_after_the_quiet_window_survives_a_worker_restart()
    {
        using var h = new Harness();
        h.Clock.Now = h.Compute.EndedAt.AddSeconds(179);
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Generations);
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await h.SweepAsync();

        var saved = Assert.IsType<DiscussionSummary>((await h.ReadAsync()).AutoSummary);
        Assert.Equal("fast", saved.Source);
        Assert.Equal(Waiting, saved.Text);
        Assert.Equal(["final"], saved.EvidenceMessageUids!);
        Assert.Equal("epoch", saved.Epoch);
        Assert.Equal(42, saved.ThroughSequence);
        Assert.Equal("fast", h.Compute.RequestBody!["qualityTier"]!.GetValue<string>());
        Assert.True(h.Compute.RequestBody["requireToolFree"]!.GetValue<bool>());
        Assert.DoesNotContain("hidden-tool-output", h.Compute.RequestBody.ToJsonString());
        Assert.Empty(h.F.Records);
        Assert.Equal(0, (await h.ReadAsync()).ConversationRevision);
        Assert.Single(h.Events.Invalidations);
        Assert.Equal("summary", h.Events.Invalidations[0]["change"]!.GetValue<string>());

        await h.NewService().SweepAsync([await h.ReadAsync()], default);
        Assert.Equal(1, h.Compute.Generations);
        Assert.Single(h.Events.Invalidations);
    }

    [Fact]
    public async Task A_summary_that_matches_the_settled_turn_is_not_reprobed_until_new_activity()
    {
        using var h = new Harness();
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await h.SweepAsync();
        Assert.NotNull((await h.ReadAsync()).AutoSummary);
        Assert.Equal(1, h.Compute.Generations);

        // The generation's own retry window ends: one probe finds the summary current.
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        var before = h.Compute.Requests;
        await h.SweepAsync();
        Assert.Equal(before + 1, h.Compute.Requests);

        // From then on it waits 30 minutes, not 2, while nothing happens.
        before = h.Compute.Requests;
        for (var minute = 2; minute < 30; minute += 2)
        {
            h.Clock.Now = h.Clock.Now.AddMinutes(2);
            await h.SweepAsync();
        }
        Assert.Equal(before, h.Compute.Requests);
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        await h.SweepAsync();
        Assert.Equal(before + 1, h.Compute.Requests);

        // New activity re-arms it at once.
        before = h.Compute.Requests;
        await h.F.Store.PatchAsync(h.F.Target.Id, new JsonObject { ["last_activity"] = "2026-11-27T21:30:00Z" });
        h.Clock.Now = h.Clock.Now.AddSeconds(1);
        await h.SweepAsync();
        Assert.True(h.Compute.Requests > before);
    }

    [Theory]
    [InlineData("working", 0)]
    [InlineData("background", 0)]
    [InlineData("awaiting_answer", 0)]
    [InlineData("idle", 1)]
    public async Task Quiet_runtime_and_empty_queue_are_both_required(string work, int queued)
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.Work = work;
        h.Compute.Depth = queued;
        await h.SweepAsync();
        Assert.Equal(0, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
    }

    [Theory]
    [InlineData("resumed")]
    [InlineData("new-evidence")]
    [InlineData("disabled")]
    [InlineData("confidential")]
    public async Task In_flight_result_is_discarded_when_its_basis_changes(string change)
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.BeforeOutput = async () =>
        {
            switch (change)
            {
                case "resumed": h.Compute.Work = "working"; break;
                case "new-evidence": h.Compute.HighWater++; break;
                case "disabled": await h.Summaries.SetDefaultAsync(h.Owner, false, default); break;
                case "confidential": await h.F.Store.PatchAsync(h.F.Target.Id, new JsonObject { ["confidential"] = true }); break;
            }
        };
        await h.SweepAsync();
        Assert.Equal(1, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
        Assert.Empty(h.Events.Invalidations);
    }

    [Fact]
    public async Task Owning_agent_can_correct_and_a_late_generation_cannot_overwrite_it()
    {
        using var h = new Harness();
        var discussion = await h.ReadAsync();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        var ownerAgent = h.Agent(h.F.AgentId);
        Assert.True(DiscussionSummaries.CanAccess(discussion, ownerAgent));
        Assert.False(DiscussionSummaries.CanAccess(discussion, h.Agent(Guid.NewGuid())));
        Assert.False(DiscussionSummaries.CanAccess(discussion, h.Person(Guid.NewGuid().ToString())));
        var edit = Waiting with { Overview = "Storage sent the updated rollout window." };
        var oldVersion = DiscussionSummaries.Version(discussion);
        h.Compute.BeforeOutput = async () =>
        {
            var (code, view) = await h.Summaries.AuthorAsync(discussion, ownerAgent, oldVersion, edit);
            Assert.Equal(200, code);
            Assert.Equal("agent", view?.Summary?.Source);
        };
        await h.SweepAsync();
        Assert.Equal(edit, (await h.ReadAsync()).AutoSummary?.Text);
        Assert.Equal(409, (await h.Summaries.AuthorAsync(discussion, ownerAgent, oldVersion, Waiting)).Status);
        Assert.Single(h.Events.Invalidations);
        Assert.Empty(h.F.Records);
    }

    [Fact]
    public async Task A_bad_model_answer_cannot_charge_indefinitely_for_the_same_settle()
    {
        using var h = new Harness();
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        h.Compute.InvalidOutput = true;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await h.SweepAsync();
            h.Clock.Now = h.Clock.Now.AddMinutes(3);
        }
        Assert.Equal(3, h.Compute.Generations);
        Assert.Null((await h.ReadAsync()).AutoSummary);
    }

    [Fact]
    public void Evidence_and_user_action_are_required_for_high_impact_labels()
    {
        var evidence = new[] { new DiscussionSummaries.Evidence("checked", "assistant", "The live test passed.") };
        Assert.False(DiscussionSummaries.TryParse(
            """{"status":"complete","label":"Done","overview":"Shipped","evidenceMessageUids":["invented"]}""", evidence, out _, out _));
        Assert.False(DiscussionSummaries.TryParse(
            """{"status":"needs_you","label":"Your turn","overview":"Waiting","evidenceMessageUids":["checked"]}""", evidence, out _, out _));
        Assert.True(DiscussionSummaries.TryParse(
            """{"status":"complete","label":"Done","overview":"Shipped","evidenceMessageUids":["checked"]}""", evidence, out _, out var accepted));
        Assert.Equal(["checked"], accepted);
    }

    // ---- Reader-aware classification, waitingOn and one-hop resolution (synthetic fixtures) ----

    private static readonly string ChainOwner = Guid.NewGuid().ToString();
    private static readonly string ChainAgent = Guid.NewGuid().ToString();
    private static readonly DateTime Settled = DateTime.Parse("2026-11-28T09:00:00Z").ToUniversalTime();

    private static DiscussionRead Discussion(string id, SummaryText? text, string? owner = null, string status = "idle",
        bool stale = false, string? title = null)
        => new(id, title ?? $"Workstream {id}", $"{id}-session", status, Settled, Settled, 4, null, owner ?? ChainOwner,
            Guid.NewGuid(), ChainAgent)
        {
            AutoSummary = text is null ? null : new DiscussionSummary($"v-{id}", text, "fast",
                new DateTimeOffset(Settled).AddMinutes(4), ChainAgent, $"{id}-session",
                stale ? Settled.AddMinutes(-5) : Settled),
        };

    private static string Answer(string status, string label, string overview, string? userAction = null,
        string? waitingOn = null, string? nextStep = null, params string[] uids)
        => JsonSerializer.Serialize(new { status, label, overview, nextStep, userAction, waitingOn, evidenceMessageUids = uids },
            DiscussionSummary.Json);

    [Fact]
    public void An_unattended_session_that_cannot_obtain_Sandbox_approval_needs_the_reader()
    {
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "ask", Content = "Launch the eval in the Sandbox." },
            new SessionMessage { Role = "assistant", EventType = "text", Phase = "final_answer", MessageUid = "blocked",
                Content = "This unattended session cannot obtain approval for the Sandbox launch. It is ready to run as soon as it is approved." },
        ]);
        Assert.Equal(["owner", "this Agent"], evidence.Select(e => e.From));

        Assert.True(DiscussionSummaries.TryParse(Answer("needs_you", "Sandbox launch approval",
            "The eval is ready; the Sandbox launch is waiting for your approval.", "Approve the Sandbox launch",
            uids: "blocked"), evidence, out var text, out var cited, "eval", reader: "Sam"));
        Assert.Equal("needs_you", text!.Status);
        Assert.Equal("Approve the Sandbox launch", text.UserAction);
        Assert.Equal("you", text.WaitingOn);
        Assert.Equal(["blocked"], cited);

        // The failure seen on 28 Nov: a third-person instruction about the reader is not an action for them.
        foreach (var thirdPerson in new[] { "Owner to obtain authorization for the Sandbox launch",
                     "The owner must approve the Sandbox launch", "User to approve the launch", "Sam to approve the launch" })
            Assert.False(DiscussionSummaries.TryParse(Answer("needs_you", "Sandbox launch approval", "Ready to launch.",
                thirdPerson, uids: "blocked"), evidence, out _, out _, "eval", reader: "Sam"), thirdPerson);
        // Waiting on "you" without saying what to do is the contradiction that hid the approvals.
        Assert.False(DiscussionSummaries.TryParse(Answer("waiting", "Sandbox launch", "Waiting for authorization.",
            waitingOn: "you", uids: "blocked"), evidence, out _, out _, "eval"));
        // A user action word that merely starts like "user" is still second person.
        Assert.True(DiscussionSummaries.TryParse(Answer("needs_you", "Usernames", "Two usernames clash.",
            "Usernames: choose which to keep", uids: "blocked"), evidence, out _, out _, "eval", reader: "Sam"));
    }

    [Fact]
    public void The_prompt_names_the_reader_and_tells_other_Agents_apart_from_them()
    {
        var prompt = DiscussionSummaries.SystemPrompt("Sam");
        Assert.Contains("Sam is the person reading this summary", prompt);
        Assert.Contains("second person", prompt);
        Assert.Contains("approval, authorization, permission prompt", prompt);
        Assert.Contains("<nova-event ... from-agent=...>", prompt);
        Assert.Contains("never the person", prompt);
        Assert.Contains("untrusted quoted evidence", prompt);
        Assert.Contains("Never invent IDs, actions or facts", prompt);
        Assert.Equal("the owner", DiscussionSummaries.ReaderName(" \u0007 "));
        Assert.Equal("Sam", DiscussionSummaries.ReaderName("Sam\"<>"));

        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "note",
                Content = "<nova-event source=\"agent-note\" from-agent=\"Atlas\" from-agent-id=\"a1\" from-discussion=\"ops-7\">\nI need the on/off decision first.\n</nova-event>" },
            new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "mail",
                Content = "<nova-event source=\"mail\">\nA newsletter arrived.\n</nova-event>" },
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "reply", Content = "Noted." },
        ]);
        Assert.Equal(["other Agent Atlas", "event mail", "this Agent"], evidence.Select(e => e.From));
    }

    [Fact]
    public async Task A_sweep_sends_the_reader_name_and_who_each_message_is_from()
    {
        using var h = new Harness();
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await h.SweepAsync();
        var system = h.Compute.RequestBody!["system"]!.GetValue<string>();
        Assert.Contains("summary-owner is the person reading this summary", system);
        var content = JsonNode.Parse(h.Compute.RequestBody["messages"]![0]!["content"]!.GetValue<string>())!;
        Assert.Equal("summary-owner", content["reader"]!.GetValue<string>());
        Assert.Equal("target", content["discussionId"]!.GetValue<string>());
        Assert.Equal("owner", content["evidence"]![0]!["from"]!.GetValue<string>());
        Assert.Equal("this Agent", content["evidence"]![1]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Decisions_held_by_a_workstream_that_needs_the_reader_resolve_to_needs_you_through_the_chain()
    {
        // A's Agent reports the decisions are held by another workstream, quoted by its discussion id.
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "held",
                Content = "Both on/off decisions are held by the workstream in discussion orbit-b; I continue once it reports back." },
        ]);
        Assert.True(DiscussionSummaries.TryParse(Answer("waiting", "Rollout switches", "Two switches wait on the orbit workstream.",
            waitingOn: "discussion:orbit-b", nextStep: "Resume after orbit-b reports.", uids: "held"), evidence, out var aText, out _, "orbit-a"));
        Assert.Equal("discussion:orbit-b", aText!.WaitingOn);
        Assert.Equal("orbit-b", aText.WaitingDiscussion);

        var a = Discussion("orbit-a", aText);
        var b = Discussion("orbit-b", new SummaryText("needs_you", "Two on/off decisions", "Both switches are ready.",
            null, "Decide whether to turn on the nightly sync and the retry queue", "you"), title: "Orbit");
        var views = DiscussionSummaries.Views([a, b], defaultEnabled: true).ToDictionary(v => v.DiscussionId);

        var resolved = views["orbit-a"];
        Assert.Equal("needs_you", resolved.Summary!.Text.Status);
        Assert.Equal("Decide whether to turn on the nightly sync and the retry queue", resolved.Summary.Text.UserAction);
        Assert.Equal("Two switches wait on the orbit workstream.", resolved.Summary.Text.Overview);
        Assert.Equal("discussion:orbit-b", resolved.Summary.Text.WaitingOn);
        Assert.Equal(new SummaryVia("orbit-b", "Orbit", "Two on/off decisions",
            "Decide whether to turn on the nightly sync and the retry queue", b.AutoSummary!.UpdatedAt), resolved.Via);
        Assert.Equal(DiscussionSummaries.Version(a), resolved.Version);
        // The stored record is untouched; only the presented view resolves.
        Assert.Equal("waiting", a.AutoSummary!.Text.Status);
        Assert.Equal("needs_you", views["orbit-b"].Summary!.Text.Status);
        Assert.Null(views["orbit-b"].Via);
    }

    [Fact]
    public void A_genuine_Agent_to_Agent_wait_stays_waiting_on_that_discussion()
    {
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "note",
                Content = "<nova-event source=\"agent-note\" from-agent=\"Atlas\" from-discussion=\"build-9\">\nThe build is running; I will send the artefact when it is done.\n</nova-event>" },
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "ack", Content = "Waiting for Atlas's artefact." },
        ]);
        Assert.True(DiscussionSummaries.TryParse(Answer("waiting", "Waiting for Atlas", "Atlas is building the artefact.",
            waitingOn: "discussion:build-9", uids: "note"), evidence, out var text, out _, "deploy-3"));
        Assert.Equal("discussion:build-9", text!.WaitingOn);

        var views = DiscussionSummaries.Views([
            Discussion("deploy-3", text),
            Discussion("build-9", new SummaryText("in_progress", "Building", "The artefact build is running.", null, null)),
        ], true).ToDictionary(v => v.DiscussionId);
        Assert.Equal("waiting", views["deploy-3"].Summary!.Text.Status);
        Assert.Null(views["deploy-3"].Summary!.Text.UserAction);
        Assert.Null(views["deploy-3"].Via);

        // Also when the other one is busy, stale, another owner's, or its summary is off.
        var needsYou = new SummaryText("needs_you", "Approval", "Ready.", null, "Approve it", "you");
        foreach (var other in new[]
                 {
                     Discussion("build-9", needsYou, status: "thinking"),
                     Discussion("build-9", needsYou, stale: true),
                     Discussion("build-9", needsYou, owner: Guid.NewGuid().ToString()),
                     Discussion("build-9", needsYou) with { AutoSummaryOverride = false },
                     Discussion("build-9", needsYou) with { Confidential = true },
                 })
        {
            var view = DiscussionSummaries.Views([Discussion("deploy-3", text), other], true).First();
            Assert.Equal("waiting", view.Summary!.Text.Status);
            Assert.Null(view.Via);
        }
    }

    [Fact]
    public void Cycles_self_references_and_unknown_ids_are_rejected()
    {
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "m", Content = "Waiting on loop-b to finish." },
        ]);
        bool Parse(string waitingOn, out SummaryText? text, IReadOnlySet<string>? known = null)
            => DiscussionSummaries.TryParse(Answer("waiting", "Waiting", "Waiting on another workstream.", waitingOn: waitingOn,
                uids: "m"), evidence, out text, out _, "loop-a", known);

        Assert.True(Parse("discussion:loop-a", out var self));
        Assert.Null(self!.WaitingOn);
        Assert.True(Parse("discussion:invented-7", out var unknown));
        Assert.Null(unknown!.WaitingOn);
        Assert.True(Parse("discussion:loop", out var partial));
        Assert.Null(partial!.WaitingOn); // "loop" only appears inside "loop-b"
        Assert.True(Parse("discussion:bad id!", out var malformed));
        Assert.Null(malformed!.WaitingOn);
        Assert.True(Parse("discussion:known-c", out var known, new HashSet<string> { "known-c" }));
        Assert.Equal("discussion:known-c", known!.WaitingOn);
        Assert.True(Parse("external", out var external));
        Assert.Equal("external", external!.WaitingOn);
        // Statuses that wait on nothing drop it; needs_you always waits on the reader.
        Assert.True(DiscussionSummaries.TryParse(Answer("in_progress", "Working", "Running.", waitingOn: "discussion:loop-b"),
            evidence, out var working, out _, "loop-a"));
        Assert.Null(working!.WaitingOn);
        Assert.False(new SummaryText("complete", "Done", "Done.", null, null, "discussion:loop-b").IsValid);
        Assert.False(new SummaryText("waiting", "Wait", "Wait.", null, null, "discussion:").IsValid);
        Assert.False(new SummaryText("waiting", "Wait", "Wait.", null, null, "someone").IsValid);

        // A cycle neither recurses nor promotes: both stay waiting, even if one end once needed the reader.
        var a = Discussion("loop-a", new SummaryText("waiting", "A", "A waits on B.", null, null, "discussion:loop-b"));
        var b = Discussion("loop-b", new SummaryText("waiting", "B", "B waits on A.", null, null, "discussion:loop-a"));
        foreach (var view in DiscussionSummaries.Views([a, b], true))
        {
            Assert.Equal("waiting", view.Summary!.Text.Status);
            Assert.Null(view.Via);
        }
        // Two hops are not followed: A -> B -> C(needs_you) leaves A waiting while B resolves.
        var c = Discussion("loop-c", new SummaryText("needs_you", "C", "C needs you.", null, "Approve C", "you"));
        var b2 = Discussion("loop-b", new SummaryText("waiting", "B", "B waits on C.", null, null, "discussion:loop-c"));
        var chain = DiscussionSummaries.Views([a, b2, c], true).ToDictionary(v => v.DiscussionId);
        Assert.Equal("waiting", chain["loop-a"].Summary!.Text.Status);
        Assert.Equal("needs_you", chain["loop-b"].Summary!.Text.Status);
        Assert.Equal("loop-c", chain["loop-b"].Via!.DiscussionId);
        // A view whose id points at itself never resolves, whatever the lookup returns.
        var selfish = Discussion("loop-a", new SummaryText("waiting", "A", "A.", null, null, "discussion:loop-a"));
        Assert.Null(DiscussionSummaries.View(selfish, true, _ => selfish).Via);
    }

    [Fact]
    public async Task Corrections_may_only_name_another_discussion_of_the_same_owner()
    {
        using var h = new Harness();
        var d = await h.ReadAsync();
        var version = DiscussionSummaries.Version(d);
        Assert.Equal(400, (await h.Summaries.AuthorAsync(d, h.Person(), version, Waiting with { WaitingOn = "discussion:target" })).Status);
        Assert.Equal(400, (await h.Summaries.AuthorAsync(d, h.Person(), version, Waiting with { WaitingOn = "discussion:nowhere" })).Status);
        var (code, view) = await h.Summaries.AuthorAsync(d, h.Person(), version,
            new SummaryText("needs_you", "Approval", "Ready to launch.", null, "Approve the launch"));
        Assert.Equal(200, code);
        Assert.Equal("you", view!.Summary!.Text.WaitingOn);
        Assert.Equal("you", (await h.ReadAsync()).AutoSummary!.Text.WaitingOn);
        Assert.Equal(view.Version, (await h.Summaries.ViewAsync(await h.ReadAsync(), default)).Version);
    }

    [Fact]
    public void Stored_records_without_waitingOn_still_load()
    {
        var data = JsonNode.Parse("""
            {"auto_summary":{"version":"old","text":{"status":"waiting","label":"Lab release","overview":"The test is ready.",
              "nextStep":"Nova runs it after handoff.","userAction":null},"source":"fast","updatedAt":"2026-11-20T10:00:00+00:00",
              "updatedBy":"agent","sessionId":"s","basisActivity":"2026-11-20T09:59:00Z","evidenceMessageUids":["final"]}}
            """)!.AsObject();
        var summary = Assert.IsType<DiscussionSummary>(DiscussionSummary.Read(data));
        Assert.Equal(Waiting, summary.Text);
        Assert.Null(summary.Text.WaitingOn);
        Assert.True(summary.Text.IsValid);
        // The new field round-trips.
        var next = summary with { Text = summary.Text with { WaitingOn = "external" } };
        var round = DiscussionSummary.Read(new JsonObject { ["auto_summary"] = JsonSerializer.SerializeToNode(next, DiscussionSummary.Json) });
        Assert.Equal("external", round!.Text.WaitingOn);
    }

    // ---- Inputs: goal, prior state, structured blocker facts, deterministic precedence, size cap ----

    private const string UnattendedDenialText =
        "Denied: this session runs at Ask with nobody to approve 'SandboxLaunch'. The installation owner can change this in RedLeaf Settings > Agent permissions, or raise this session in its Info panel. Continue without it.";

    private static SessionMessage ToolUse(string uid, string tool) => new() { Role = "assistant", EventType = "tool_use", MessageUid = uid, ToolName = tool, ToolInput = "{\"secret\":\"input\"}" };
    private static SessionMessage ToolResult(string uid, string tool, string content) => new() { Role = "assistant", EventType = "tool_result", MessageUid = uid, ToolName = tool, Content = content };

    [Fact]
    public void Permission_denials_and_agent_notes_become_one_line_facts_without_tool_output()
    {
        var facts = DiscussionSummaries.Facts([
            ToolUse("t1", "Bash"),
            ToolResult("t1", "Bash", "total 48\nsecret-file.txt\nraw tool output"),
            ToolUse("t2", "SandboxLaunch"),
            ToolResult("t2", "SandboxLaunch", UnattendedDenialText),
            ToolUse("t3", "Write"),
            ToolResult("t3", "Write", "The user denied 'Write'. Do not retry it; continue another way or ask the user."),
            ToolUse("t4", "WebFetch"),
            ToolResult("t4", "WebFetch", "Denied: this session runs at Auto and nobody approved 'WebFetch' within 10 minutes. The installation owner can change this. Continue without it, or ask again when the user is back."),
            ToolUse("t5", "WebFetch"), // retried later: that denial is superseded
            new() { Role = "user", EventType = "user_message", MessageUid = "note",
                Content = "<nova-event source=\"agent-note\" from-agent=\"Atlas\" from-agent-id=\"a1\" from-discussion=\"build-9\">\nThe artefact build   is running.\n</nova-event>" },
            new() { Role = "user", EventType = "user_message", MessageUid = "mail", Content = "<nova-event source=\"mail\">\nNewsletter\n</nova-event>" },
        ]);

        Assert.Equal(["SandboxLaunch", "Write", "WebFetch"], facts.Permissions.Select(p => p.Tool));
        Assert.Equal(["denied_unattended", "denied_by_you", "denied_unanswered"], facts.Permissions.Select(p => p.Outcome));
        Assert.Equal([true, false, false], facts.Permissions.Select(p => p.OwnerMustAct));
        Assert.True(facts.Permissions[2].Superseded);
        Assert.Equal("SandboxLaunch was denied because nobody was there to approve it (session runs at Ask).", facts.Permissions[0].Fact);
        Assert.Equal("You denied Write.", facts.Permissions[1].Fact);
        var note = Assert.Single(facts.AgentNotes);
        Assert.Equal(new DiscussionSummaries.AgentNoteFact("other Agent Atlas", "build-9", "The artefact build is running.", "note"), note);
        Assert.Equal(new DiscussionSummaries.Blocker("SandboxLaunch", "SandboxLaunch needs your approval",
            "Approve SandboxLaunch for this discussion, or tell it to continue without it", "t2"), facts.Blocker);

        var json = JsonSerializer.Serialize(facts, DiscussionSummary.Json);
        Assert.DoesNotContain("raw tool output", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("Settings > Agent permissions", json);
    }

    [Fact]
    public void A_later_retry_or_the_owner_writing_supersedes_an_unattended_denial()
    {
        Assert.Null(DiscussionSummaries.Facts([ToolResult("t", "SandboxLaunch", UnattendedDenialText), ToolUse("t2", "SandboxLaunch")]).Blocker);
        Assert.Null(DiscussionSummaries.Facts([ToolResult("t", "SandboxLaunch", UnattendedDenialText),
            new() { Role = "user", EventType = "user_message", MessageUid = "u", Content = "Go ahead without it." }]).Blocker);
        // Another Agent's note or the Agent's own text is not the owner and does not supersede it.
        Assert.NotNull(DiscussionSummaries.Facts([ToolResult("t", "SandboxLaunch", UnattendedDenialText),
            new() { Role = "user", EventType = "user_message", MessageUid = "n", Content = "<nova-event source=\"agent-note\" from-agent=\"Atlas\">\nok\n</nova-event>" },
            new() { Role = "assistant", EventType = "text", MessageUid = "a", Content = "I could not launch it." }]).Blocker);
        // A tool whose output merely mentions a denial deeper in its text is not a denial.
        Assert.Empty(DiscussionSummaries.Facts([ToolResult("t", "Read", "log:\n" + UnattendedDenialText)]).Permissions);
    }

    [Theory]
    [InlineData("waiting", null, "Waiting for Sandbox access")]
    [InlineData("complete", null, "Launched")]
    [InlineData("in_progress", null, "Launching")]
    [InlineData("needs_you", "Owner to obtain authorization for the Sandbox launch", "Authorization")]
    public void A_blocker_fact_makes_it_needs_you_whatever_the_model_wrote(string status, string? userAction, string label)
    {
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "said", Content = "The Sandbox launch is pending." },
        ]);
        var blocker = new DiscussionSummaries.Blocker("SandboxLaunch", "SandboxLaunch needs your approval",
            "Approve SandboxLaunch for this discussion, or tell it to continue without it", "t2");
        // Without the fact a "complete" with no evidence would be refused; with it the fact decides.
        Assert.True(DiscussionSummaries.TryParse(Answer(status, label, "The eval is staged for the Sandbox.", userAction),
            evidence, out var text, out var cited, "eval", reader: "Sam", blocker: blocker));
        Assert.Equal("needs_you", text!.Status);
        Assert.Equal("you", text.WaitingOn);
        Assert.Equal(blocker.Action, text.UserAction);
        Assert.Equal(blocker.Label, text.Label);
        Assert.Equal("The eval is staged for the Sandbox.", text.Overview);
        Assert.Equal("t2", cited[0]);
    }

    [Fact]
    public void A_blocker_keeps_the_models_own_second_person_wording()
    {
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "said", Content = "Blocked on approval." },
        ]);
        var blocker = new DiscussionSummaries.Blocker("SandboxLaunch", "SandboxLaunch needs your approval", "Approve SandboxLaunch", "t2");
        Assert.True(DiscussionSummaries.TryParse(Answer("needs_you", "Sandbox launch approval", "Ready to launch.",
            "Approve the Sandbox launch", uids: "said"), evidence, out var text, out var cited, "eval", reader: "Sam", blocker: blocker));
        Assert.Equal("Approve the Sandbox launch", text!.UserAction);
        Assert.Equal("Sandbox launch approval", text.Label);
        Assert.Equal(["t2", "said"], cited);
    }

    [Fact]
    public async Task A_sweep_gives_goal_prior_state_and_facts_and_the_fact_decides_the_status()
    {
        using var h = new Harness();
        // The discussion's goal is its first persisted person's message, read from Nova's own records.
        h.F.Records.Add(new(1, h.F.Target.Id, "user", "Launch the nightly eval in the Sandbox and report the scores.",
            new JsonObject { ["source"] = "user-message" }, DateTimeOffset.UtcNow));
        h.F.Records.Add(new(2, h.F.Target.Id, "user", "A later message.", new JsonObject { ["source"] = "user-message" }, DateTimeOffset.UtcNow));
        await h.F.Store.PatchAsync(h.F.Target.Id, new JsonObject
        {
            [DiscussionSummary.DataKey] = JsonSerializer.SerializeToNode(new DiscussionSummary("old",
                new SummaryText("waiting", "Lab", "Owner to obtain authorization.", null, null), "fast",
                DateTimeOffset.Parse("2026-11-27T20:00:00Z"), "agent", "older-session", DateTime.Parse("2026-11-27T20:00:00Z")),
                DiscussionSummary.Json),
        });
        h.Compute.Window.AddRange([
            ToolUse("t2", "SandboxLaunch"),
            ToolResult("t2", "SandboxLaunch", UnattendedDenialText),
            new() { Role = "user", EventType = "user_message", MessageUid = "note",
                Content = "<nova-event source=\"agent-note\" from-agent=\"Atlas\" from-discussion=\"build-9\">\nArtefact ready.\n</nova-event>" },
        ]);
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await h.SweepAsync();

        Assert.Contains($"limit={DiscussionSummaries.WindowRecords}", h.Compute.TranscriptPath);
        var content = h.Compute.RequestBody!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.True(content.Length <= DiscussionSummaries.MaxInputChars);
        var input = JsonNode.Parse(content)!;
        Assert.Equal("first message", input["goal"]!["kind"]!.GetValue<string>());
        Assert.Equal("Launch the nightly eval in the Sandbox and report the scores.", input["goal"]!["text"]!.GetValue<string>());
        Assert.Equal("owner", input["goal"]!["from"]!.GetValue<string>());
        Assert.StartsWith("Previous summary: prior state only", input["priorSummary"]!["note"]!.GetValue<string>());
        Assert.Equal("Owner to obtain authorization.", input["priorSummary"]!["overview"]!.GetValue<string>());
        var permission = input["facts"]!["permissions"]![0]!;
        Assert.Equal("denied_unattended", permission["outcome"]!.GetValue<string>());
        Assert.True(permission["ownerMustAct"]!.GetValue<bool>());
        Assert.Equal("build-9", input["facts"]!["agentNotes"]![0]!["discussionId"]!.GetValue<string>());
        Assert.Empty(input["facts"]!["delegations"]!.AsArray());
        Assert.DoesNotContain("Settings > Agent permissions", content);

        // The model answered "waiting" (the gateway's canned answer); the fact makes it needs_you.
        var saved = (await h.ReadAsync()).AutoSummary!;
        Assert.Equal("needs_you", saved.Text.Status);
        Assert.Equal("you", saved.Text.WaitingOn);
        Assert.Equal("Approve SandboxLaunch for this discussion, or tell it to continue without it", saved.Text.UserAction);
        Assert.Equal("The test is ready.", saved.Text.Overview);
        Assert.Contains("t2", saved.EvidenceMessageUids!);
    }

    [Fact]
    public void A_brief_from_another_Agent_is_the_goal_when_it_comes_first()
    {
        var goal = DiscussionSummaries.Goal(new(1, Guid.NewGuid(), "user",
            "<nova-event source=\"agent-note\" from-agent=\"Atlas\" from-discussion=\"ops-1\">\n" + new string('x', 900) + "\n</nova-event>",
            new JsonObject { ["source"] = "agent-note", ["idempotency_key"] = "discussion-brief:d1" }, DateTimeOffset.UtcNow))!;
        Assert.Equal("brief", goal.Kind);
        Assert.Equal("other Agent Atlas", goal.From);
        Assert.Equal(600, goal.Text.Length);
        Assert.Null(DiscussionSummaries.Goal(null));
    }

    [Fact]
    public void The_model_input_is_bounded()
    {
        var text = new string('a', 850);
        var evidence = Enumerable.Range(0, 14).Select(i => new DiscussionSummaries.Evidence($"uid-{i:D2}", "user", text, "other Agent " + new string('n', 80))).ToArray();
        var permissions = Enumerable.Range(0, DiscussionSummaries.MaxPermissionFacts).Select(i => new DiscussionSummaries.PermissionFact(
            new string('t', 60), "denied_unattended", new string('f', 200), false, true, $"p{i}",
            Guid.NewGuid().ToString(), new string('w', 60), DateTimeOffset.UtcNow)).ToArray();
        var delegations = Enumerable.Range(0, DiscussionSummaries.MaxDelegations).Select(i => new DiscussionSummaries.DelegationFact(
            Guid.NewGuid().ToString(), new string('d', 120), "blocked", new string('b', 60))).ToArray();
        var notes = Enumerable.Range(0, DiscussionSummaries.MaxAgentNotes).Select(i => new DiscussionSummaries.AgentNoteFact(
            "other Agent " + new string('n', 80), new string('i', 80), new string('e', 200), $"n{i}")).ToArray();
        var related = Enumerable.Range(0, 8).Select(i => new DiscussionSummaries.RelatedDiscussion(new string('r', 80), new string('t', 120))).ToArray();
        var full = new DiscussionSummaries.SummaryInput("Sam", new string('i', 80), new string('T', 200), "completed",
            new("first message", "owner", new string('g', 600)),
            new("Previous summary: prior state only.", "waiting", new string('l', 64), new string('o', 240), null, null, DateTimeOffset.UtcNow),
            new(permissions, delegations, notes), related, evidence);

        // Every field at its own limit at once: the cap holds by dropping at most the oldest excerpt.
        var json = DiscussionSummaries.Input(full);
        Assert.True(json.Length <= DiscussionSummaries.MaxInputChars, json.Length.ToString());
        var all = JsonNode.Parse(json)!["evidence"]!.AsArray();
        Assert.True(all.Count >= 13, all.Count.ToString());
        Assert.Equal("uid-13", all[^1]!["messageUid"]!.GetValue<string>());
        // Without worker facts, everything fits as it is.
        var parentOnly = full with { Facts = full.Facts with { Permissions = full.Facts.Permissions.Select(p => p with { WorkerSession = null, Worker = null, At = null }).ToArray() } };
        Assert.Equal(14, JsonNode.Parse(DiscussionSummaries.Input(parentOnly))!["evidence"]!.AsArray().Count);

        // Text that escapes heavily (non-ASCII) is trimmed from the oldest evidence to stay under the cap.
        var escaped = full with { Evidence = evidence.Select(e => e with { Text = new string('é', 850) }).ToArray() };
        var bounded = DiscussionSummaries.Input(escaped);
        Assert.True(bounded.Length <= DiscussionSummaries.MaxInputChars, bounded.Length.ToString());
        var kept = JsonNode.Parse(bounded)!["evidence"]!.AsArray();
        Assert.True(kept.Count < 14);
        Assert.Equal("uid-13", kept[^1]!["messageUid"]!.GetValue<string>());
    }

    // ---- Denials inside delegated worker sessions, and the conversation's language ----

    private static readonly DateTime Denied = DateTime.Parse("2026-11-28T08:00:00Z").ToUniversalTime();

    private static SessionMessage At(SessionMessage message, DateTime at) { message.Timestamp = at; return message; }
    private static SessionMessage Owner(string text, DateTime at)
        => At(new() { Role = "user", EventType = "user_message", MessageUid = "owner-" + at.Ticks, Content = text }, at);
    private static DiscussionSummaries.WorkerTail Worker(DateTimeOffset? delegatedAt, params SessionMessage[] messages)
        => new("worker-1", "Sandbox eval run", delegatedAt, messages);
    private static SessionMessage[] WorkerDenial() => [
        At(ToolUse("w1", "SandboxLaunch"), Denied.AddSeconds(-1)),
        At(ToolResult("w1", "SandboxLaunch", UnattendedDenialText), Denied),
        At(new() { Role = "assistant", EventType = "text", MessageUid = "w2", Content = "I could not launch the Sandbox." }, Denied.AddSeconds(5)),
    ];

    [Fact]
    public void A_worker_denial_needs_the_reader_until_the_parent_supersedes_it()
    {
        var delegatedAt = new DateTimeOffset(Denied.AddMinutes(-10));
        var parent = new[] { At(new() { Role = "assistant", EventType = "text", MessageUid = "p", Content = "Delegated the eval." }, Denied.AddMinutes(-9)) };

        var facts = DiscussionSummaries.Facts(parent, [Worker(delegatedAt, WorkerDenial())]);
        var fact = Assert.Single(facts.Permissions);
        Assert.Equal(("worker-1", "Sandbox eval run", true), (fact.WorkerSession, fact.Worker, fact.OwnerMustAct));
        Assert.Equal("SandboxLaunch was denied in delegated work \"Sandbox eval run\" because nobody was there to approve it (session runs at Ask).", fact.Fact);
        Assert.Equal(new DiscussionSummaries.Blocker("SandboxLaunch", "SandboxLaunch needs your approval",
            "Approve SandboxLaunch for the delegated work \"Sandbox eval run\", here in this discussion, or tell it to continue without it",
            null, "worker-1"), facts.Blocker);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(facts, DiscussionSummary.Json)); // no tool input carried

        // The owner writing in the parent after the denial supersedes it; before it does not.
        Assert.Null(DiscussionSummaries.Facts([.. parent, Owner("Skip the Sandbox, use the local run.", Denied.AddMinutes(1))],
            [Worker(delegatedAt, WorkerDenial())]).Blocker);
        Assert.NotNull(DiscussionSummaries.Facts([Owner("Run the eval in the Sandbox.", Denied.AddMinutes(-20)), .. parent],
            [Worker(delegatedAt, WorkerDenial())]).Blocker);
        // A later delegation to the same worker supersedes it.
        Assert.Null(DiscussionSummaries.Facts(parent, [Worker(new DateTimeOffset(Denied.AddMinutes(2)), WorkerDenial())]).Blocker);
        // Inside the worker, the same rules: a retry of the tool supersedes it.
        Assert.Null(DiscussionSummaries.Facts(parent,
            [Worker(delegatedAt, [.. WorkerDenial(), At(ToolUse("w3", "SandboxLaunch"), Denied.AddMinutes(1))])]).Blocker);

        // The parent's own unsuperseded denial and a worker's are both facts; the newest decides.
        var both = DiscussionSummaries.Facts(
            [At(ToolResult("p1", "Bash", "Denied: this session runs at Ask with nobody to approve 'Bash'. Continue without it."), Denied.AddMinutes(-30))],
            [Worker(delegatedAt, WorkerDenial())]);
        Assert.Equal(["Bash", "SandboxLaunch"], both.Permissions.Select(p => p.Tool));
        Assert.Equal("worker-1", both.Blocker!.WorkerSession);
    }

    [Fact]
    public void Fallback_wording_follows_a_French_conversation()
    {
        Assert.Equal("fr", DiscussionSummaries.Language([
            "Lance l'évaluation dans la Sandbox et donne-moi les scores.",
            "Je n'ai pas pu lancer la Sandbox : la session ne peut pas obtenir l'autorisation.",
        ]));
        Assert.Equal("en", DiscussionSummaries.Language(["Launch the eval in the Sandbox and send me the scores.", "I could not launch it."]));
        Assert.Equal("en", DiscussionSummaries.Language([null, "ok"]));

        var delegatedAt = new DateTimeOffset(Denied.AddMinutes(-10));
        var worker = DiscussionSummaries.Facts([], [Worker(delegatedAt, WorkerDenial())], "fr").Blocker!;
        Assert.Equal("SandboxLaunch attend votre accord", worker.Label);
        Assert.Equal("Autorisez SandboxLaunch pour le travail délégué « Sandbox eval run », dans cette discussion, ou dites-lui de continuer sans", worker.Action);
        var own = DiscussionSummaries.Facts([ToolResult("t", "SandboxLaunch", UnattendedDenialText)], language: "fr").Blocker!;
        Assert.Equal("Autorisez SandboxLaunch pour cette discussion, ou dites-lui de continuer sans", own.Action);

        // Through precedence: a French model answer that is not needs_you gets the French fallback.
        var evidence = DiscussionSummaries.Excerpts([
            new SessionMessage { Role = "assistant", EventType = "text", MessageUid = "dit", Content = "Le lancement est en attente." },
        ]);
        Assert.True(DiscussionSummaries.TryParse(Answer("waiting", "En attente", "L'évaluation attend la Sandbox."),
            evidence, out var text, out var cited, "eval", reader: "Laurent", blocker: worker));
        Assert.Equal(("needs_you", "SandboxLaunch attend votre accord", worker.Action), (text!.Status, text.Label, text.UserAction));
        Assert.Empty(cited); // the worker's record is not this transcript's evidence, and that is enough
        Assert.True(worker.Action.Length <= 200);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sweep_reads_delegated_workers_and_a_later_owner_message_supersedes_their_denial(bool ownerWroteLater)
    {
        using var h = new Harness();
        var target = await h.ReadAsync();
        // The delegation marker Nova records in the parent, and the worker's ownership metadata.
        var marker = new DiscussionMessage(1, h.F.Target.Id, "system", "delegated", new JsonObject
        {
            ["source"] = "event:delegation",
            ["parts_json"] = JsonSerializer.Serialize(new[] { new { type = "event_data", data = new { sessionId = "worker-1", status = "started" } } }),
        }, new DateTimeOffset(Denied.AddMinutes(-10)));
        var workerEntity = CallbackDeliveryTests.Entity("ai-session", "session-worker-1", new()
        {
            ["session_id"] = "worker-1", ["user_id"] = target.OwnerId, ["owner_agent_id"] = target.AgentId, ["confidential"] = false,
        }) with { Name = "Sandbox eval run" };
        var messages = CallbackDeliveryTests.Proxy.Create<IDiscussions>((method, args) =>
        {
            Assert.Equal("GetMessagesAsync", method);
            return Task.FromResult<IReadOnlyList<DiscussionMessage>>((long)args[2]! == 0 ? [marker] : []);
        });
        var entities = CallbackDeliveryTests.Proxy.Create<IEntityStore>((method, args) => method switch
        {
            "GetAsync" => Task.FromResult((LeafEntity?)((Guid)args[0]! == workerEntity.Id ? workerEntity : null)),
            "QueryAsync" => Task.FromResult<IReadOnlyList<LeafEntity>>(((EntityQuery)args[0]!).TypeSlug == "ai-session" ? [workerEntity] : []),
            _ => throw new NotSupportedException(method),
        });
        var summaries = h.NewService(new DelegationActivity(messages, new RedComputeClient(h.Compute), entities));
        h.Compute.Workers["worker-1"] = [.. WorkerDenial()];
        if (ownerWroteLater) h.Compute.Window.Add(Owner("Skip the Sandbox, run it locally.", Denied.AddMinutes(3)));
        h.Clock.Now = h.Compute.EndedAt.AddMinutes(3);
        await summaries.SweepAsync([await h.ReadAsync()], default);

        // One tail read of the worker, no cursor.
        var path = Assert.Single(h.Compute.WorkerPaths);
        Assert.Equal($"/ai-session/sessions/worker-1/transcript-page?limit={DiscussionSummaries.WorkerWindowRecords}", path);
        var content = h.Compute.RequestBody!["messages"]![0]!["content"]!.GetValue<string>();
        var permission = JsonNode.Parse(content)!["facts"]!["permissions"]![0]!;
        Assert.Equal("worker-1", permission["workerSession"]!.GetValue<string>());
        Assert.Equal(!ownerWroteLater, permission["ownerMustAct"]!.GetValue<bool>());
        Assert.DoesNotContain("secret", content);

        var saved = (await h.ReadAsync()).AutoSummary!;
        if (ownerWroteLater)
        {
            Assert.Equal("waiting", saved.Text.Status); // the model's own answer stands
            Assert.Null(saved.Text.UserAction);
        }
        else
        {
            Assert.Equal("needs_you", saved.Text.Status);
            Assert.Equal("Approve SandboxLaunch for the delegated work \"Sandbox eval run\", here in this discussion, or tell it to continue without it",
                saved.Text.UserAction);
        }
    }

    private sealed class Harness : IDisposable
    {
        public CallbackDeliveryTests.Fixture F { get; }
        public string Owner { get; }
        public TestClock Clock { get; } = new();
        public Gateway Compute { get; }
        public RecordingEvents Events { get; } = new();
        public DiscussionSummaries Summaries { get; }
        private readonly Records preferences = new();

        public Harness()
        {
            var owner = CallbackDeliveryTests.Entity("user", "summary-owner", new());
            Owner = owner.Id.ToString();
            F = new(extraEntities: [owner]);
            F.Store.PatchAsync(F.Target.Id, new JsonObject
            {
                ["owner_id"] = Owner, ["status"] = "idle", ["last_activity"] = "2026-11-27T21:00:00Z",
            }).GetAwaiter().GetResult();
            Compute = new(Owner, F.AgentId.ToString());
            Summaries = NewService();
        }

        public DiscussionSummaries NewService(DelegationActivity? delegations = null) => new(F.Store, F.Entities, preferences, F.Agents,
            new RedComputeClient(Compute), Events, NullLogger<DiscussionSummaries>.Instance, Clock, delegations);

        public async Task<DiscussionRead> ReadAsync() => (await F.Store.GetAsync(F.Target.Id))!;
        public async Task SweepAsync() => await Summaries.SweepAsync([await ReadAsync()], default);
        public HttpContext Person(string? owner = null) => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", owner ?? Owner)], "test-human")),
        };
        public HttpContext Agent(Guid id)
        {
            var identity = JsonSerializer.Serialize(new
            {
                app = new { id = "nova" }, actor = new { kind = "agent", id = id.ToString(), entityId = id.ToString() },
                beneficiary = new { kind = "user", id = Owner },
            });
            return new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new("sub", Owner), new("token_use", "execution"), new("execution_identity", identity)], "test-agent")),
            };
        }
        public void Dispose() => F.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-27T21:05:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Gateway(string owner, string agent) : IComputeGateway
    {
        public DateTimeOffset EndedAt { get; } = DateTimeOffset.Parse("2026-11-27T21:01:00Z");
        public string Work { get; set; } = "idle";
        public int Depth { get; set; }
        public long HighWater { get; set; } = 42;
        public int Requests { get; private set; }
        public int Generations { get; private set; }
        public JsonNode? RequestBody { get; private set; }
        public Func<Task>? BeforeOutput { get; set; }
        /// <summary>Records appended after the default window, newest last.</summary>
        public List<SessionMessage> Window { get; } = [];
        /// <summary>Worker sessions' transcripts by session id, served as tail pages.</summary>
        public Dictionary<string, List<SessionMessage>> Workers { get; } = [];
        public List<string> WorkerPaths { get; } = [];
        public string? TranscriptPath { get; private set; }
        public bool InvalidOutput { get; set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            Requests++;
            Assert.NotNull(provenance);
            Assert.Equal(owner, provenance.OnBehalfOf.Id);
            Assert.Equal("startup", provenance.Origin.Entrypoint.Kind);
            var path = request.RequestUri!.OriginalString;
            if (path.EndsWith("?tail=1", StringComparison.Ordinal))
                return Json(new
                {
                    session = new
                    {
                        id = "target-session", workState = Work, status = "Idle", userId = owner, ownerAgentId = agent,
                        confidential = false, lastTurn = new { turnId = "settled", outcome = "completed", endedAt = EndedAt },
                    },
                    inputQueue = new { depth = Depth },
                });
            if (Workers.Keys.FirstOrDefault(id => path.StartsWith($"/ai-session/sessions/{id}/transcript-page", StringComparison.Ordinal)) is { } worker)
            {
                WorkerPaths.Add(path);
                return Json(new
                {
                    session = new { id = worker, status = "Idle" },
                    page = new { epoch = "worker-epoch", boundaryComplete = true, throughSequence = 9 },
                    messages = Workers[worker],
                });
            }
            if (path.Contains("transcript-page", StringComparison.Ordinal) && (TranscriptPath ??= path) is not null)
                return Json(new
                {
                    session = new { id = "target-session", status = "Idle" },
                    page = new { epoch = "epoch", boundaryComplete = true, throughSequence = HighWater },
                    messages = new[]
                    {
                        new SessionMessage { Role = "user", EventType = "user_message", MessageUid = "prompt", Content = "Run the browser test." },
                        new SessionMessage { Role = "assistant", EventType = "tool_result", MessageUid = "final", Content = "hidden-tool-output" },
                        new SessionMessage { Role = "assistant", EventType = "text", Phase = "final_answer", MessageUid = "final", Content = "The test is ready and waiting for the lab." },
                    }.Concat(Window).ToArray(),
                });
            Assert.Equal("/ai-session/generate", path);
            Generations++;
            RequestBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (BeforeOutput is not null) await BeforeOutput();
            if (InvalidOutput) return Json(new { success = true, text = "invalid" });
            var output = JsonSerializer.SerializeToNode(Waiting, DiscussionSummary.Json)!.AsObject();
            output["evidenceMessageUids"] = new JsonArray("final");
            return Json(new { success = true, text = output.ToJsonString() });
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, DiscussionSummary.Json),
                    System.Text.Encoding.UTF8, "application/json"),
            };
    }

    private sealed class Records : IRecordStreams
    {
        private readonly List<LeafRecord> rows = [];
        public Task AppendAsync(string stream, JsonObject data, Guid? entityId = null, string? userId = null, CancellationToken ct = default)
        {
            rows.Add(new(rows.Count + 1, stream, entityId, userId, data, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LeafRecord>> QueryAsync(string stream, RecordQuery? query = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LeafRecord>>(rows.Where(r => r.Stream == stream && r.UserId == query?.UserId)
                .Reverse().Take(query?.Limit ?? 100).ToArray());
    }

    private sealed class RecordingEvents : IPluginEvents
    {
        public List<JsonObject> Invalidations { get; } = [];
        public Task PublishAsync(string type, JsonObject payload, CancellationToken ct = default)
        {
            Assert.Equal("discussion.changed", type);
            Invalidations.Add(payload);
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => throw new NotSupportedException();
    }
}
