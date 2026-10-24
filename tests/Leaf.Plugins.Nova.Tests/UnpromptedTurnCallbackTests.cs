using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// RedCompute reports, through a discussion's session-scoped question subscription, the end of a turn
// the delegated session started without a queued prompt (incident 7f614fcd534a: a peer message woke
// the CLI after every prompt of 855ddaca had completed). The discussion must read it as exactly that,
// once per turn, distinct from any prompted completion.
public sealed class UnpromptedTurnCallbackTests
{
    private const string Subscription = "session-subscription:0123456789ABCDEF";

    [Fact]
    public async Task Unprompted_turn_reads_as_a_turn_the_session_started_on_its_own()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Report(f, sequence: 1));

        var message = Assert.Single(f.TargetMessages);
        Assert.Contains("type=\"session-unprompted-turn\"", message.Content);
        Assert.Contains("Session delegated-session finished a turn it started on its own (leaf-capabilities)", message.Content);
        Assert.DoesNotContain("completed", message.Content);
        Assert.Contains("Delegated session finished a turn it started on its own: leaf-capabilities", Assert.Single(f.LiveMessages).Content);
        using var parts = JsonDocument.Parse(message.Metadata["parts_json"]!.GetValue<string>());
        var data = parts.RootElement.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "event_data").GetProperty("data");
        Assert.Equal("unprompted", data.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task Each_unprompted_turn_is_delivered_once()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Report(f, sequence: 1));
        Assert.Equal(200, await Report(f, sequence: 1));
        Assert.Single(f.TargetMessages);
        Assert.Single(f.LiveMessages);
        Assert.Single(f.Gateway.Admitted);

        Assert.Equal(200, await Report(f, sequence: 2));
        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Equal(2, f.LiveMessages.Count());
        Assert.Equal(2, f.Gateway.Admitted.Count);
    }

    [Fact]
    public async Task Unprompted_turn_stays_distinct_from_the_prompted_turns_of_the_same_session()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await f.Complete("prompt-one", deliveredMessageUid: "prompt-one", promptMessageUid: "prompt-one", executionObserved: true));
        Assert.Equal(200, await Report(f, sequence: 1));

        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Contains("type=\"session-complete\"", f.TargetMessages.First().Content);
        Assert.Contains("type=\"session-unprompted-turn\"", f.TargetMessages.Last().Content);
    }

    [Fact]
    public async Task Unprompted_turn_ended_by_a_stop_keeps_the_stop_wording_and_says_whose_turn_it_was()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Report(f, sequence: 1, status: "Ended", stopReason: "user_stopped", reason: "stopped"));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("type=\"session-complete\"", content);
        Assert.Contains("ended with status: Ended (in a turn it started on its own)", content);
    }

    [Fact]
    public async Task Unprompted_turn_with_queued_prompts_says_what_runs_next()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Report(f, sequence: 1, pendingInputs: 2));

        Assert.Contains("finished a turn it started on its own (leaf-capabilities); it has 2 more queued prompts, which it will run next",
            Assert.Single(f.TargetMessages).Content);
    }

    private static async Task<int> Report(CallbackDeliveryTests.Fixture f, int sequence, string status = "Idle",
        string? stopReason = null, string? reason = null, int pendingInputs = 0)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.QueryString = new("?discussionId=target");
        ctx.Request.ContentType = "application/json";
        // The payload RedCompute builds for an unprompted turn (SessionPromptCallbacks.UnpromptedPayload).
        var body = new JsonObject
        {
            ["sessionId"] = "delegated-session", ["callbackId"] = Subscription, ["promptMessageUid"] = null,
            ["status"] = status, ["reason"] = reason, ["stopReason"] = stopReason, ["userId"] = "test-owner",
            ["deliveredMessageUid"] = null, ["executionObserved"] = true, ["workState"] = "idle",
            ["pendingInputs"] = pendingInputs, ["trigger"] = "unprompted", ["title"] = "leaf-capabilities",
            ["backgroundTasksRunning"] = 0, ["deliverySequence"] = sequence,
        };
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
        var result = await CallbackEndpoints.HandleSessionCompleteAsync(ctx, f.Store, f.Injector, f.Live);
        return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
    }
}
