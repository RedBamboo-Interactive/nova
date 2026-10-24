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
