using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LastTurn = Leaf.Plugins.Nova.RedComputeClient.SessionLastTurn;
using Verdict = Leaf.Plugins.Nova.TransientTurnVerdict;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 2026-11-14: a DNS outage. LIVE (1b98ac8a, session 3bd8d51a7d2c) and two orchestrators' own turns
/// ended failed with "Can't reach the API server ... (ENOTFOUND)" once the CLI's retry gave up, and
/// sat idle for three hours until Laurent typed "You got interrupted, continue".
/// </summary>
public sealed class TransientTurnRecoveryTests
{
    private const string Enotfound = "API Error: Can't reach the API server — check your internet or DNS (ENOTFOUND)";
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 14, 7, 3, 43, TimeSpan.Zero);

    // ---- The decision table ----

    [Fact]
    public void A_transient_failure_waits_for_its_backoff_then_wakes_as_attempt_one()
    {
        var notYet = Qualify(now: FailedAt.AddSeconds(30));
        Assert.Equal(Verdict.NotYet, notYet.Verdict);
        Assert.Equal(FailedAt + TransientTurnRecoveryPolicy.Delays[0], notYet.DueAt);

        var wake = Qualify(now: FailedAt + TransientTurnRecoveryPolicy.Delays[0]);
        Assert.Equal(Verdict.Wake, wake.Verdict);
        Assert.Equal(1, wake.Attempt);
    }

    [Theory]
    [InlineData("completed", true, "Idle")]
    [InlineData("interrupted", true, "Idle")]
    [InlineData("failed", false, "Idle")]   // a refusal, a usage limit: RedCompute says not transient
    [InlineData("failed", true, "Active")]  // something already runs
    [InlineData("failed", true, "Stopped")] // stopped by someone
    public void Nothing_but_an_idle_transient_failure_is_retried(string outcome, bool transient, string status)
        => Assert.Equal(Verdict.None, Qualify(probe: Probe(outcome: outcome, transient: transient, status: status)).Verdict);

    [Fact]
    public void Idle_parking_does_not_cancel_the_retry()
        => Assert.Equal(Verdict.Wake, Qualify(probe: Probe(status: "Stopped", stopReason: "idle_timeout")).Verdict);

    [Theory]
    [InlineData("heartbeat")]
    public void Heartbeat_runs_again_on_its_own_and_is_left_alone(string type)
        => Assert.Equal(Verdict.None, Qualify(discussion: Discussion(type: type)).Verdict);

    [Theory]
    [InlineData(DiscussionStatus.Archived)]
    [InlineData(DiscussionStatus.Archiving)]
    public void A_closed_discussion_never_wakes(string status)
        => Assert.Equal(Verdict.None, Qualify(discussion: Discussion(status: status)).Verdict);

    [Fact]
    public void A_failure_older_than_the_window_is_left_alone()
        => Assert.Equal(Verdict.None, Qualify(now: FailedAt + TransientTurnRecoveryPolicy.Window + TimeSpan.FromSeconds(1)).Verdict);

    [Fact]
    public void The_same_failed_turn_is_never_handled_twice()
        => Assert.Equal(Verdict.None, Qualify(previous: Record("t1", 1, FailedAt.AddMinutes(1))).Verdict);

    [Fact]
    public void The_woken_turn_failing_again_continues_the_count_until_the_retries_run_out()
    {
        var wokenAt = FailedAt.AddMinutes(1);
        var again = Probe(turnId: "t2", endedAt: wokenAt.AddMinutes(4));
        var second = Qualify(probe: again, previous: Record("t1", 1, wokenAt), now: wokenAt.AddMinutes(4) + TransientTurnRecoveryPolicy.Delays[1]);
        Assert.Equal(Verdict.Wake, second.Verdict);
        Assert.Equal(2, second.Attempt);

        var last = TransientTurnRecoveryPolicy.Delays.Length;
        Assert.Equal(Verdict.Exhausted,
            Qualify(probe: again, previous: Record("t1", last, wokenAt), now: wokenAt.AddHours(1)).Verdict);
    }

    [Fact]
    public void A_new_failure_long_after_the_last_wake_starts_the_count_over()
    {
        var wokenAt = FailedAt.AddMinutes(1);
        var later = Probe(turnId: "t9", endedAt: wokenAt + TransientTurnRecoveryPolicy.ChainGap + TimeSpan.FromMinutes(5));
        var decision = Qualify(probe: later, previous: Record("t1", 5, wokenAt), now: later.EndedAt!.Value.AddMinutes(1));
        Assert.Equal(Verdict.Wake, decision.Verdict);
        Assert.Equal(1, decision.Attempt);
    }

    [Fact]
    public void The_wake_says_what_failed_and_what_to_do()
    {
        var content = TransientTurnRecoveryPolicy.Content(FailedAt, Enotfound, 2);
        Assert.StartsWith("Your last turn failed at 08:03 Zurich time (07:03:43Z) on a transient network or provider error: API Error", content);
        Assert.Contains("ENOTFOUND", content);
        Assert.Contains("redo only what didn't finish", content);
        Assert.Contains("Automatic retry 2 of 6.", content);
    }

    // ---- The loop ----

    [Fact]
    public async Task LIVE_is_woken_once_per_failed_turn_and_an_exhausted_chat_tells_its_owner_once()
    {
        var now = FailedAt + TransientTurnRecoveryPolicy.Delays[0];
        var live = Discussion(id: "1b98ac8a", sessionId: "3bd8d51a7d2c", type: "live");
        var chat = Discussion(id: "6a6be852", sessionId: "069f571f36a9");
        var quiet = Discussion(id: "quiet", sessionId: "s-quiet");
        var port = new FakePort([live, chat, quiet]);
        port.Probes["3bd8d51a7d2c"] = Probe(turnId: "live-1");
        port.Probes["069f571f36a9"] = Probe(turnId: "chat-1");
        port.Probes["s-quiet"] = Probe(outcome: "completed", transient: false);
        var recovery = new TransientTurnRecovery(port, NullLogger.Instance, () => now);

        Assert.Equal(2, await recovery.RunOnceAsync(default));
        Assert.Equal(["1b98ac8a", "6a6be852"], port.Wakes.Select(w => w.DiscussionId).Order());
        var wake = port.Wakes.Single(w => w.DiscussionId == "1b98ac8a");
        Assert.Equal("transient-retry:1b98ac8a:3bd8d51a7d2c:live-1", wake.Key);
        Assert.Equal(1, wake.Metadata.GetProperty("attempt").GetInt32());
        Assert.Equal(Enotfound, wake.Metadata.GetProperty("error").GetString());

        // The next pass, or a restarted Nova, wakes nothing more for the same turns.
        Assert.Equal(0, await recovery.RunOnceAsync(default));
        Assert.Equal(2, port.Wakes.Count);

        // The chat keeps failing until the budget is spent: its owner hears it once, in LIVE.
        port.Records[chat.EntityId] = Record("chat-5", TransientTurnRecoveryPolicy.Delays.Length, now);
        port.Probes["069f571f36a9"] = Probe(turnId: "chat-6", endedAt: now.AddMinutes(4));
        now = now.AddMinutes(30);
        Assert.Equal(0, await recovery.RunOnceAsync(default));
        Assert.Equal(0, await recovery.RunOnceAsync(default));
        var notice = Assert.Single(port.Notices);
        Assert.Equal("6a6be852", notice.DiscussionId);
        Assert.Contains("6 automatic retries didn't get through", notice.Content);
        Assert.Equal(2, port.Wakes.Count);
    }

    // ---- helpers ----

    private static TransientTurnRecoveryPolicy.Decision Qualify(DiscussionRead? discussion = null, LastTurn? probe = null,
        JsonNode? previous = null, DateTimeOffset? now = null)
        => TransientTurnRecoveryPolicy.Qualify(discussion ?? Discussion(), probe ?? Probe(), previous,
            now ?? FailedAt + TransientTurnRecoveryPolicy.Delays[0]);

    private static LastTurn Probe(string outcome = "failed", bool transient = true, string status = "Idle",
        string? stopReason = null, string turnId = "t1", DateTimeOffset? endedAt = null)
        => new(true, status, stopReason, turnId, outcome, endedAt ?? FailedAt, outcome == "failed" ? Enotfound : null, transient);

    private static DiscussionRead Discussion(string id = "917b2dfe", string sessionId = "49d71f1e105a",
        string status = DiscussionStatus.Idle, string type = "chat")
        => new(id, "Songes Carmin UI design system audit", sessionId, status, FailedAt.UtcDateTime.AddHours(-3),
            FailedAt.UtcDateTime, 15, null, "owner-1", Guid.NewGuid(), "nova-agent", type);

    private static JsonObject Record(string turnId, int attempt, DateTimeOffset at)
        => new() { ["turnId"] = turnId, ["attempt"] = attempt, ["at"] = at.ToString("O"), ["state"] = "woken" };

    private sealed record Sent(string DiscussionId, string Content, JsonElement Metadata, string Key);

    private sealed class FakePort(IReadOnlyList<DiscussionRead> discussions) : ITransientTurnRecoveryPort
    {
        public Dictionary<string, LastTurn> Probes { get; } = [];
        public Dictionary<Guid, JsonNode?> Records { get; } = [];
        public List<Sent> Wakes { get; } = [];
        public List<Sent> Notices { get; } = [];

        public Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct) => Task.FromResult(discussions);

        public Task<LastTurn> ProbeAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(Probes.TryGetValue(sessionId, out var p) ? p : new LastTurn(false, null));

        // Mirrors DiscussionStore.TryClaimTransientRetryAsync.
        public Task<TransientTurnRecoveryPolicy.Decision?> TryClaimAsync(DiscussionRead discussion, LastTurn probe,
            DateTimeOffset now, CancellationToken ct)
        {
            var decision = TransientTurnRecoveryPolicy.Qualify(discussion, probe, Records.GetValueOrDefault(discussion.EntityId), now);
            if (decision.Verdict is Verdict.Wake or Verdict.Exhausted)
                Records[discussion.EntityId] = new JsonObject
                {
                    ["turnId"] = probe.TurnId, ["attempt"] = decision.Attempt, ["at"] = now.ToString("O"),
                    ["state"] = decision.Verdict == Verdict.Wake ? "woken" : "exhausted",
                };
            return Task.FromResult<TransientTurnRecoveryPolicy.Decision?>(decision);
        }

        public Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct)
        {
            Wakes.Add(new(discussion.Id, content, metadata, key));
            return Task.FromResult(true);
        }

        public Task NotifyOwnerAsync(DiscussionRead discussion, string content, string key, CancellationToken ct)
        {
            Notices.Add(new(discussion.Id, content, default, key));
            return Task.CompletedTask;
        }
    }
}
