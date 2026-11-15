using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// RedCompute reports how a delegated turn ended (turnOutcome), who interrupted it (interruptedBy) and
// which turn it was (turnId, shared by every prompt one turn carried). An interrupted turn is never
// rendered as a completion, and one turn's completions arrive in the discussion once.
public sealed class TurnOutcomeCallbackTests
{
    [Fact]
    public async Task Interrupted_turn_says_who_interrupted_it_and_never_that_it_completed()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true,
            pendingInputs: 1, turnId: "p1", turnOutcome: "interrupted", interruptSource: "interrupt-current", interruptedBy: "stop"));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-interrupted\"", content);
        Assert.Contains("interruptSource=\"interrupt-current\"", content);
        Assert.Contains("turn was interrupted by prompt stop (interrupt-current)", content);
        Assert.Contains("it did not complete; it has 1 more queued prompt, which it will run next", content);
        Assert.DoesNotContain("completed", content);
        var live = Assert.Single(f.LiveMessages).Content;
        Assert.Contains("Delegated session turn was interrupted by prompt stop (interrupt-current): same title", live);
        Assert.DoesNotContain("completed", live);
    }

    [Theory]
    [InlineData("send-now", "next", "by send-now for prompt next")]
    [InlineData("interrupt", null, "by a stop request")]
    public async Task Every_interrupt_source_is_named(string source, string? by, string expected)
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true,
            turnId: "p1", turnOutcome: "interrupted", interruptSource: source, interruptedBy: by);

        Assert.Contains(expected, Assert.Single(f.TargetMessages).Content);
    }

    // 2026-11-14: a DNS outage. The worker's turn failed and the session went idle; the discussion
    // and LIVE were told "Delegated session completed".
    [Fact]
    public async Task Failed_turn_says_it_failed_with_the_provider_error_and_never_that_it_completed()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        const string error = "API Error: Can't reach the API server — check your internet or DNS (ENOTFOUND)";

        Assert.Equal(200, await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true,
            turnId: "p1", turnOutcome: "failed", turnError: error));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-failed\"", content);
        Assert.Contains($"Session delegated-session turn failed (same title): {error}; it did not complete", content);
        Assert.DoesNotContain("completed", content);
        var live = Assert.Single(f.LiveMessages).Content;
        Assert.Contains("Delegated session failed: same title (API Error: Can't reach the API server", live);
        Assert.DoesNotContain("completed", live);
    }

    // 2026-11-15: the Fable allowance ran out. The worker's turn failed on it and the orchestrator was
    // told "Session cd6e22042916 completed". The exact Claude Code message.
    internal const string FableLimit = "You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich). Switch to another model, or manage usage credits at https://claude.ai/settings/usage?from=cc_cli_limit_message, to continue.";

    [Theory]
    [InlineData("usage_limit", "usage_limit")] // current RedCompute: error kind and Claude's stop reason
    [InlineData(null, "usage_limit")]          // an older RedCompute: the stop reason alone
    [InlineData("usage_limit", null)]          // a provider that sets no stop reason
    public async Task A_turn_that_hit_a_usage_limit_failed_with_its_reason_and_is_neither_completed_nor_paused(
        string? kind, string? stopReason)
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("p1", stopReason: stopReason, deliveredMessageUid: "p1", promptMessageUid: "p1",
            executionObserved: true, turnId: "p1", turnOutcome: "failed", turnError: FableLimit, turnErrorKind: kind));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-failed\"", content);
        Assert.Contains("turn failed (same title): You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich).", content);
        Assert.Contains("a provider usage limit, not the session's work", content);
        Assert.DoesNotContain("completed", content);
        Assert.DoesNotContain("paused", content);
        var live = Assert.Single(f.LiveMessages).Content;
        Assert.StartsWith("Provider usage limit hit at ", live);
        Assert.Contains("(\"same title\" stopped on it): You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich).", live);
        Assert.DoesNotContain("https://", live);
    }

    [Fact]
    public async Task Every_session_one_usage_limit_stops_reports_failed_but_LIVE_reads_it_once()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("a", stopReason: "usage_limit", deliveredMessageUid: "a", promptMessageUid: "a", executionObserved: true,
            turnId: "a", turnOutcome: "failed", turnError: FableLimit, turnErrorKind: "usage_limit", sessionId: "worker-a");
        await f.Complete("b", stopReason: "usage_limit", deliveredMessageUid: "b", promptMessageUid: "b", executionObserved: true,
            turnId: "b", turnOutcome: "failed", turnError: FableLimit, turnErrorKind: "usage_limit", sessionId: "worker-b");

        Assert.Equal(2, f.TargetMessages.Count(m => m.Content.Contains("type=\"session-failed\"")));
        Assert.Single(f.LiveMessages);
    }

    [Fact]
    public async Task A_failed_sign_in_is_said_as_one_and_a_plain_failure_keeps_its_own_line()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("a", stopReason: "authentication_failed", deliveredMessageUid: "a", promptMessageUid: "a", executionObserved: true,
            turnId: "a", turnOutcome: "failed", turnError: "Invalid API key · Please run /login", turnErrorKind: "authentication");
        await f.Complete("b", deliveredMessageUid: "b", promptMessageUid: "b", executionObserved: true,
            turnId: "b", turnOutcome: "failed", turnError: "Prompt is too long", turnErrorKind: "other", sessionId: "worker-b");

        var lines = f.LiveMessages.Select(m => m.Content).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Provider sign-in failed at ", lines[0]);
        Assert.Contains("Delegated session failed: same title (Prompt is too long)", lines[1]);
    }

    // The usage_limit pause RedCompute holds a registration across is a Stopped session; it still reads as a pause.
    [Fact]
    public async Task A_stopped_usage_limit_session_without_a_failed_turn_still_reads_as_paused()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("p1", status: "Stopped", stopReason: "usage_limit", deliveredMessageUid: "p1", promptMessageUid: "p1",
            executionObserved: true);

        Assert.Contains("paused — usage limit reached", Assert.Single(f.TargetMessages).Content);
    }

    [Fact]
    public async Task A_failure_after_RedCompute_retries_says_so_and_a_successful_retry_reads_as_a_completion()
    {
        using var failed = new CallbackDeliveryTests.Fixture();
        await failed.Complete("r6", deliveredMessageUid: "r6", promptMessageUid: "r6", executionObserved: true,
            turnId: "r6", turnOutcome: "failed", turnError: "API Error: Connection error.", trigger: "transient-retry");
        var content = Assert.Single(failed.TargetMessages).Content;
        Assert.Contains("turn failed (same title): API Error: Connection error.; it did not complete", content);
        Assert.Contains("RedCompute had already retried it after a transient network or provider failure", content);

        using var retried = new CallbackDeliveryTests.Fixture();
        await retried.Complete("r1", deliveredMessageUid: "r1", promptMessageUid: "r1", executionObserved: true,
            turnId: "r1", turnOutcome: "completed", trigger: "transient-retry");
        var done = Assert.Single(retried.TargetMessages).Content;
        Assert.Contains("type=\"session-complete\"", done);
        Assert.Contains("(after RedCompute retried it following a transient network or provider failure)", done);
    }

    [Fact]
    public async Task A_completed_turn_reads_as_before()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("stop", deliveredMessageUid: "stop", promptMessageUid: "stop", executionObserved: true,
            turnId: "stop", turnOutcome: "completed");

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-complete\"", content);
        Assert.Contains("Session delegated-session completed: same title", content);
    }

    [Fact]
    public async Task Steered_prompt_and_the_turn_it_joined_arrive_as_one_event()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        // Two registrations, one turn: the steer has its own deliveredMessageUid but the turn's id.
        Assert.Equal(200, await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true,
            turnId: "p1", turnOutcome: "completed"));
        Assert.Equal(200, await f.Complete("steer", deliveredMessageUid: "steer", promptMessageUid: "steer", executionObserved: true,
            turnId: "p1", turnOutcome: "completed"));

        Assert.Single(f.TargetMessages);
        Assert.Single(f.LiveMessages);
    }

    [Fact]
    public async Task Without_a_turn_id_a_steered_completion_still_arrives_on_its_own()
    {
        // An older RedCompute: no turnId, so the turns are told apart by deliveredMessageUid.
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true);
        await f.Complete("steer", deliveredMessageUid: "steer", promptMessageUid: "steer", executionObserved: true);

        Assert.Equal(2, f.TargetMessages.Count());
    }

    [Fact]
    public async Task Interrupted_turn_and_the_interrupting_prompts_own_turn_are_separate_events()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await f.Complete("p1", deliveredMessageUid: "p1", promptMessageUid: "p1", executionObserved: true,
            turnId: "p1", turnOutcome: "interrupted", interruptSource: "interrupt-current", interruptedBy: "stop");
        await f.Complete("stop", deliveredMessageUid: "stop", promptMessageUid: "stop", executionObserved: true,
            turnId: "stop", turnOutcome: "completed");

        Assert.Equal(["session-interrupted", "session-complete"],
            f.TargetMessages.Select(m => m.Content.Contains("type=\"session-interrupted\"") ? "session-interrupted" : "session-complete"));
    }
}
