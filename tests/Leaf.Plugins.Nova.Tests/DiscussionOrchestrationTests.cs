using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// An Orchestrated discussion's settled turn arms a quiet window; when it ends and the session is still idle,
// its Agent's Heartbeat is told. Pacing slows the hand-offs down instead of capping them.
public sealed class DiscussionOrchestrationTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static LeafEntity Heartbeat(Guid agentId)
        => CallbackDeliveryTests.Entity("discussion", DiscussionStore.Slug("heartbeat"), new()
        {
            ["discussion_id"] = "heartbeat", ["app"] = "nova", ["type"] = "heartbeat", ["status"] = "idle",
            ["agent"] = agentId.ToString(), ["session_id"] = "heartbeat-session", ["owner_id"] = "test-owner",
        });

    private sealed class Harness : IDisposable
    {
        public CallbackDeliveryTests.Fixture F { get; }
        public Guid HeartbeatId { get; }
        public Clock Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.Zero));
        public OrchestrationSweeper Sweeper { get; }

        public Harness(bool heartbeat = true, bool orchestrated = true)
        {
            LeafEntity? hb = null;
            F = new CallbackDeliveryTests.Fixture(agentEntities: agent => heartbeat ? [hb = Heartbeat(agent)] : []);
            HeartbeatId = hb?.Id ?? Guid.Empty;
            if (orchestrated)
                Assert.NotNull(F.Store.SetOrchestratedAsync(F.Target.Id, true).GetAwaiter().GetResult());
            Sweeper = new OrchestrationSweeper(F.Store, F.Entities, F.Injector, new RedComputeClient(F.Gateway), clock: Clock);
        }

        public IEnumerable<DiscussionMessage> HeartbeatMessages => F.Records.Where(m => m.DiscussionId == HeartbeatId);
        public OrchestrationState State => OrchestrationState.Read(F.TargetData);

        public async Task<int> Settled(string? turnId = "turn-1", string status = "Idle", int? pendingInputs = null,
            string? turnOutcome = "completed", int? backgroundTasksRunning = null, string? outcome = null)
        {
            var body = new JsonObject
            {
                ["sessionId"] = "target-session", ["status"] = status, ["callbackId"] = "orch-prompt-1",
                ["promptMessageUid"] = "prompt-1", ["turnId"] = turnId, ["turnOutcome"] = turnOutcome,
            };
            if (pendingInputs is not null) body["pendingInputs"] = pendingInputs;
            if (backgroundTasksRunning is not null) body["backgroundTasksRunning"] = backgroundTasksRunning;
            if (outcome is not null) body["outcome"] = outcome;
            // The route's branch is covered below; here the clock is what the test controls.
            var parsed = JsonDocument.Parse(body.ToJsonString()).RootElement;
            var result = await DiscussionOrchestration.HandleCallbackAsync(parsed, "target", F.Store, F.Entities, Clock, default);
            return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        }

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task A_settled_turn_arms_a_three_minute_window_and_tells_nobody_yet()
    {
        using var h = new Harness();

        Assert.Equal(200, await h.Settled());

        Assert.Equal(h.Clock.Now + TimeSpan.FromMinutes(3), h.State.DueAt);
        Assert.Equal("turn-1", h.State.TurnKey);
        Assert.Empty(h.F.Records);
        Assert.Equal(0, await h.Sweeper.SweepAsync());
    }

    [Fact]
    public async Task When_the_window_ends_on_an_idle_session_the_Heartbeat_is_told_once()
    {
        using var h = new Harness();
        await h.Settled();
        h.Clock.Now += TimeSpan.FromMinutes(3);

        Assert.Equal(1, await h.Sweeper.SweepAsync());
        Assert.Equal(0, await h.Sweeper.SweepAsync());

        var note = Assert.Single(h.HeartbeatMessages).Content;
        Assert.Contains("type=\"orchestrated-idle\"", note);
        Assert.Contains("discussion=\"target\"", note);
        Assert.Contains("heartbeat-orchestration Skill", note);
        Assert.Contains("Hand-off 1 since its person last wrote there", note);
        Assert.Empty(h.F.TargetMessages);
        Assert.Empty(h.F.LiveMessages);
        Assert.Equal((null, null, 1), (h.State.DueAt, h.State.TurnKey, h.State.Chain));
    }

    [Fact]
    public async Task A_session_working_again_loses_the_armed_hand_off()
    {
        using var h = new Harness();
        await h.Settled();
        h.Clock.Now += TimeSpan.FromMinutes(5);
        h.F.Gateway.SessionStatus = "Active";

        Assert.Equal(0, await h.Sweeper.SweepAsync());

        Assert.Empty(h.F.Records);
        Assert.Null(h.State.DueAt);
    }

    [Fact]
    public async Task New_input_cancels_the_window_and_a_persons_message_resets_the_pacing()
    {
        using var h = new Harness();
        await DiscussionOrchestration.UpdateAsync(h.F.Entities, h.F.Target.Id, _ => new(h.Clock.Now, "turn-9", 7), default);
        var discussion = (await h.F.Store.GetAsync("target"))!;
        var client = new RedComputeClient(CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        await DiscussionOrchestration.RegisterPromptAsync(client, h.F.Entities, discussion, "s", "uid-a", fromPerson: false, null, default);
        Assert.Equal((null, 7), (h.State.DueAt, h.State.Chain));

        await DiscussionOrchestration.RegisterPromptAsync(client, h.F.Entities, discussion, "s", "uid-b", fromPerson: true, null, default);
        Assert.Equal(0, h.State.Chain);
    }

    [Fact]
    public async Task The_same_turn_reported_again_keeps_its_deadline()
    {
        using var h = new Harness();
        await h.Settled("turn-1");
        var first = h.State.DueAt;
        h.Clock.Now += TimeSpan.FromMinutes(2);

        await h.Settled("turn-1");

        Assert.Equal(first, h.State.DueAt);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 3)]   // the first five hand-offs since he wrote keep the 3-minute window
    [InlineData(5, 6)]
    [InlineData(6, 12)]
    [InlineData(7, 24)]
    [InlineData(8, 48)]
    [InlineData(9, 60)]  // then an hour, never a stop
    [InlineData(40, 60)]
    public void Pacing_doubles_after_five_hand_offs_up_to_an_hour(int chain, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), DiscussionOrchestration.Window(chain));

    [Fact]
    public async Task A_paced_chain_arms_a_longer_window()
    {
        using var h = new Harness();
        await DiscussionOrchestration.UpdateAsync(h.F.Entities, h.F.Target.Id, _ => new(null, null, 6), default);

        await h.Settled();

        Assert.Equal(h.Clock.Now + TimeSpan.FromMinutes(12), h.State.DueAt);
    }

    [Theory]
    [InlineData(2, "completed", null, null, "Idle")]        // more input queued: not idle yet
    [InlineData(null, "interrupted", null, null, "Idle")]   // someone stopped it
    [InlineData(null, "stopped", null, null, "Idle")]
    [InlineData(null, "completed", 1, null, "Idle")]        // background work will wake it
    [InlineData(null, "completed", null, "paused", "Idle")]
    [InlineData(null, "completed", null, null, "Stopped")]  // the session itself stopped
    public async Task Only_a_turn_that_settled_with_nothing_left_arms_anything(
        int? pending, string turnOutcome, int? background, string? outcome, string status)
    {
        using var h = new Harness();

        Assert.Equal(200, await h.Settled(pendingInputs: pending, turnOutcome: turnOutcome,
            backgroundTasksRunning: background, outcome: outcome, status: status));

        Assert.Null(h.State.DueAt);
    }

    [Fact]
    public async Task A_failed_turn_is_still_handed_over_and_says_so()
    {
        using var h = new Harness();
        await h.Settled(turnOutcome: "failed");
        h.Clock.Now += TimeSpan.FromMinutes(3);

        await h.Sweeper.SweepAsync();

        Assert.Contains("Its last turn failed.", Assert.Single(h.HeartbeatMessages).Content);
    }

    [Fact]
    public async Task A_discussion_no_longer_orchestrated_is_left_alone()
    {
        using var h = new Harness(orchestrated: false);

        await h.Settled();

        Assert.Null(h.State.DueAt);
    }

    [Fact]
    public async Task Clearing_the_mark_drops_the_armed_state()
    {
        using var h = new Harness();
        await h.Settled();

        await h.F.Store.SetOrchestratedAsync(h.F.Target.Id, false);

        Assert.Equal(OrchestrationState.Empty, h.State);
    }

    [Fact]
    public async Task Without_an_open_Heartbeat_nothing_is_sent()
    {
        using var h = new Harness(heartbeat: false);
        await h.Settled();
        h.Clock.Now += TimeSpan.FromMinutes(3);

        Assert.Equal(0, await h.Sweeper.SweepAsync());

        Assert.Empty(h.F.Records);
        Assert.Null(h.State.DueAt);
    }

    [Fact]
    public async Task Marking_it_on_while_idle_hands_it_over_at_once()
    {
        using var h = new Harness(orchestrated: false);
        var discussion = (await h.F.Store.SetOrchestratedAsync(h.F.Target.Id, true))!;

        await DiscussionOrchestration.HandOffNowAsync(h.F.Store, h.F.Entities, h.F.Injector, discussion, default);

        Assert.Contains("was just marked Orchestrated", Assert.Single(h.HeartbeatMessages).Content);
        Assert.Equal(1, h.State.Chain);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, DiscussionOrchestration.NoHeartbeat)]
    public async Task An_Agent_without_an_open_Heartbeat_cannot_orchestrate(bool heartbeat, string? refusal)
    {
        using var h = new Harness(heartbeat: heartbeat, orchestrated: false);

        var discussion = (await h.F.Store.GetAsync("target"))!;

        Assert.Equal(refusal, await DiscussionOrchestration.RefusalAsync(h.F.Store, discussion, default));
    }

    [Fact]
    public void Live_Heartbeat_and_confidential_discussions_cannot_be_orchestrated()
    {
        var chat = Chat();
        Assert.Null(DiscussionOrchestration.Refusal(chat));
        Assert.NotNull(DiscussionOrchestration.Refusal(chat with { Type = "live" }));
        Assert.NotNull(DiscussionOrchestration.Refusal(chat with { Type = "heartbeat" }));
        Assert.NotNull(DiscussionOrchestration.Refusal(chat with { Confidential = true }));
        Assert.NotNull(DiscussionOrchestration.Refusal(chat with { AgentId = null }));
    }

    [Fact]
    public void Only_an_active_orchestrated_discussion_carries_the_context_line()
    {
        Assert.Empty(DiscussionOrchestration.ContextLines(Chat()));
        Assert.Single(DiscussionOrchestration.ContextLines(Chat() with { Orchestrated = true }));
        Assert.Empty(DiscussionOrchestration.ContextLines(Chat() with { Orchestrated = true, Status = DiscussionStatus.Archiving }));
    }

    [Fact]
    public async Task The_mark_and_its_state_reach_the_wire()
    {
        using var h = new Harness();
        await h.Settled();

        var read = (await h.F.Store.GetAsync("target"))!;
        Assert.True(read.Orchestrated);
        var info = JsonSerializer.SerializeToElement(DiscussionStore.ToInfo(read));
        Assert.True(info.GetProperty("orchestrated").GetBoolean());
        Assert.Equal(h.Clock.Now + TimeSpan.FromMinutes(3), info.GetProperty("orchestration").GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(0, info.GetProperty("orchestration").GetProperty("chain").GetInt32());
    }

    [Fact]
    public async Task Each_prompt_registers_its_own_durable_idle_callback()
    {
        using var h = new Harness();
        var requests = new List<(string Url, string Body)>();
        var client = new RedComputeClient(CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, args) =>
        {
            var request = (HttpRequestMessage)args[0]!;
            requests.Add((request.RequestUri!.ToString(), request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var discussion = (await h.F.Store.GetAsync("target"))!;

        await DiscussionOrchestration.RegisterPromptAsync(client, h.F.Entities, discussion with { Orchestrated = false }, "s1", "uid-1", false, null, default);
        Assert.Empty(requests);

        await DiscussionOrchestration.RegisterPromptAsync(client, h.F.Entities, discussion, "s1", "uid-1", false, null, default);

        var (url, body) = Assert.Single(requests);
        Assert.EndsWith("/ai-session/sessions/s1/callback", url);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("orch-uid-1", json.RootElement.GetProperty("callbackId").GetString());
        Assert.Equal("uid-1", json.RootElement.GetProperty("promptMessageUid").GetString());
        Assert.EndsWith("callbacks/session-complete?discussionId=target&orchestrated=1", json.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_session_complete_route_branches_on_the_orchestrated_flag()
    {
        using var h = new Harness();
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.QueryString = new("?discussionId=target&orchestrated=1");
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"sessionId":"target-session","status":"Idle","callbackId":"orch-p","promptMessageUid":"p","turnId":"t"}"""));

        var result = await CallbackEndpoints.HandleSessionCompleteAsync(ctx, h.F.Store, h.F.Injector, h.F.Live);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
        Assert.NotNull(h.State.DueAt);
        Assert.Empty(h.F.Records); // no delegation event in the discussion, no LIVE line
    }

    private static DiscussionRead Chat() => new("c1", "A chat", "s1", DiscussionStatus.Idle,
        DateTime.UtcNow, DateTime.UtcNow, 0, null, "owner", Guid.NewGuid(), "agent-1");
}
