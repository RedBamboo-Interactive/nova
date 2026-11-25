using System.Net;
using System.Text.Json;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// A delegated turn can end Idle while its background work (a subagent, a background shell) keeps
// running and later wakes the session. That is not a finished delegation: RedCompute reports the
// turn with backgroundTasksRunning, keeps the registration, and reports the woken turn under the
// next deliverySequence.
public sealed class BackgroundTurnDelegationTests
{
    [Theory]
    [InlineData("background", "running")]
    [InlineData("idle", "finished")]
    [InlineData(null, "finished")] // an older RedCompute without workState keeps today's projection
    public void Idle_with_running_background_work_is_still_running(string? workState, string expected)
        => Assert.Equal(expected, DelegationActivity.ProjectStatus(
            new("Idle", null, null, "owner", "agent", false, null, 0, "empty", null, null, workState)));

    [Theory]
    [InlineData("background", "running")]
    [InlineData("idle", "finished")]
    public async Task Snapshot_reads_workState_from_RedCompute_instead_of_counting_tasks(string workState, string expected)
    {
        var gateway = CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    session = new
                    {
                        id = "eb0d256c663d", status = "Idle", userId = "owner", ownerAgentId = "agent", confidential = false, workState,
                        backgroundTasks = new[]
                        {
                            new { taskId = "b1", kind = "shell", status = "running" },
                            new { taskId = "b2", kind = "agent", status = "completed" },
                            new { taskId = "b3", kind = "shell", status = "lost" },
                        },
                    },
                    inputQueue = new { depth = 0, state = "empty" },
                })),
            }));

        var snapshot = (await new RedComputeClient(gateway).GetDelegationSnapshotAsync("eb0d256c663d")).Value;

        Assert.NotNull(snapshot);
        Assert.Equal(workState, snapshot!.WorkState);
        // The running task in the payload is not re-counted: workState decides.
        Assert.Equal(expected, DelegationActivity.ProjectStatus(snapshot));
    }

    [Fact]
    public async Task Turn_ending_with_background_work_is_not_reported_as_completed()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 2, deliverySequence: 1, workState: "background"));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-turn-ended\"", content);
        Assert.Contains("backgroundTasksRunning=\"2\"", content);
        Assert.Contains("2 background tasks still running", content);
        Assert.DoesNotContain("completed", content);
        var live = Assert.Single(f.LiveMessages).Content;
        Assert.Contains("Delegated session turn ended with 2 background tasks still running: same title", live);
        Assert.DoesNotContain("completed", live);
    }

    // 2026-11-25 audit: these said "nothing to do yet" and still cost the supervisor a turn each.
    [Fact]
    public async Task Turn_ending_with_background_work_waits_for_the_supervisors_next_turn()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 2, deliverySequence: 1, workState: "background"));
        Assert.Single(f.TargetMessages);
        Assert.Empty(f.Gateway.Admitted);

        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 0, deliverySequence: 2, workState: "idle"));

        Assert.Equal(2, f.Gateway.Admitted.Count);
        using var completion = System.Text.Json.JsonDocument.Parse(f.Gateway.Bodies[0]);
        using var released = System.Text.Json.JsonDocument.Parse(f.Gateway.Bodies[1]);
        Assert.Contains("session-complete", completion.RootElement.GetProperty("content").GetString());
        Assert.Contains("session-turn-ended", released.RootElement.GetProperty("content").GetString());
        Assert.Equal("steer-current", released.RootElement.GetProperty("delivery").GetString());
    }

    [Fact]
    public async Task A_pause_RedCompute_continues_by_itself_waits_too_and_a_plain_pause_does_not()
    {
        using var held = new CallbackDeliveryTests.Fixture();
        Assert.Equal(200, await held.Complete("prompt-one", "Stopped", "maintenance_restart", autoContinue: true));
        Assert.Empty(held.Gateway.Admitted);

        using var plain = new CallbackDeliveryTests.Fixture();
        Assert.Equal(200, await plain.Complete("prompt-one", "Stopped", "maintenance_restart"));
        Assert.Single(plain.Gateway.Admitted);
    }

    [Fact]
    public async Task Plain_completion_without_workState_is_unchanged()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("prompt-one"));

        Assert.Contains("type=\"session-complete\"", Assert.Single(f.TargetMessages).Content);
        Assert.Contains("Delegated session completed: same title", Assert.Single(f.LiveMessages).Content);
    }

    [Fact]
    public async Task Retry_of_the_background_turn_is_deduplicated_and_the_woken_turn_still_completes()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 1, deliverySequence: 1, workState: "background"));
        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 1, deliverySequence: 1, workState: "background"));
        Assert.Single(f.TargetMessages);
        Assert.Single(f.LiveMessages);

        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 0, deliverySequence: 2, workState: "idle"));
        Assert.Equal(200, await f.Complete("prompt-one", backgroundTasksRunning: 0, deliverySequence: 2, workState: "idle"));

        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Contains("type=\"session-complete\"", f.TargetMessages.Last().Content);
        Assert.Equal(2, f.LiveMessages.Count());
        Assert.Contains("Delegated session completed", f.LiveMessages.Last().Content);
        Assert.Equal(2, f.Gateway.Admitted.Count);
    }
}
