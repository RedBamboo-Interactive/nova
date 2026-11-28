using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// An Orchestrated discussion settles when its current summary is complete or needs its person, or when its
// Heartbeat settles it: nothing is armed and the Heartbeat is not told again until new input reaches it.
public sealed partial class DiscussionSummaryTests
{
    private static readonly SummaryText Complete = Waiting with { Status = "complete", NextStep = null };
    private static readonly SummaryText NeedsYouText = Waiting with
    {
        Status = "needs_you", WaitingOn = SummaryText.You, UserAction = "Approve the lab booking",
    };

    private static async Task<Harness> OrchestratedAsync(Func<string, IEnumerable<LeafEntity>>? more = null)
    {
        var h = new Harness(heartbeat: true, more);
        Assert.NotNull(await h.F.Store.SetOrchestratedAsync(h.F.Target.Id, true));
        await h.Summaries.SetDefaultAsync(h.Owner, true, default);
        return h;
    }

    /// <summary>Stores a current summary as generation would have, without the save path's own settling.</summary>
    private static async Task StoreSummaryAsync(Harness h, SummaryText text)
    {
        var d = await h.ReadAsync();
        var summary = new DiscussionSummary("stored", text, "fast", h.Clock.Now, h.F.AgentId.ToString(), d.SessionId!, d.LastActivity);
        await h.F.Store.PatchAsync(h.F.Target.Id, new JsonObject
        {
            [DiscussionSummary.DataKey] = JsonSerializer.SerializeToNode(summary, DiscussionSummary.Json),
        });
    }

    private static async Task ArmAsync(Harness h)
        => await DiscussionOrchestration.UpdateAsync(h.F.Entities, h.F.Target.Id, _ => new(h.Clock.Now, "turn-1", 0), default);

    private static OrchestrationSweeper Sweeper(Harness h) => new(h.F.Store, h.F.Entities, h.F.Injector,
        new RedComputeClient(h.F.Gateway), clock: h.Clock, summaries: h.Summaries, events: h.Events);

    private static OrchestrationState State(Harness h) => OrchestrationState.Read(h.F.TargetData);
    private static IEnumerable<DiscussionMessage> HeartbeatNotes(Harness h) => h.F.Records.Where(m => m.DiscussionId != h.F.Target.Id);

    [Theory]
    [InlineData("complete")]
    [InlineData("needs_you")]
    public async Task A_complete_or_needs_you_summary_settles_it_instead_of_handing_it_over(string status)
    {
        using var h = await OrchestratedAsync();
        await StoreSummaryAsync(h, status == "complete" ? Complete : NeedsYouText);
        await ArmAsync(h);
        var sweeper = Sweeper(h);

        Assert.Equal(0, await sweeper.SweepAsync());

        var state = State(h);
        Assert.Equal((status, DiscussionSummaries.SettledBySummary), (state.Settled, state.SettledBy));
        Assert.Equal((null, null, 0), (state.DueAt, state.TurnKey, state.Revisits));
        Assert.Empty(HeartbeatNotes(h));
        Assert.Contains(h.Events.Invalidations, e => e["change"]?.GetValue<string>() == "orchestration");

        // Hours later: still parked, no watch, no hand-off.
        h.Clock.Now += TimeSpan.FromHours(5);
        Assert.Equal(0, await sweeper.SweepAsync());
        Assert.Empty(HeartbeatNotes(h));
        Assert.Null(State(h).DueAt);
    }

    [Fact]
    public async Task A_waiting_summary_keeps_it_ongoing_and_handed_over()
    {
        using var h = await OrchestratedAsync();
        await StoreSummaryAsync(h, Waiting);
        await ArmAsync(h);

        Assert.Equal(1, await Sweeper(h).SweepAsync());

        Assert.False(State(h).IsSettled);
        Assert.Contains("orchestrated-idle", Assert.Single(HeartbeatNotes(h)).Content);
        Assert.True(DiscussionOrchestration.IsWatch(State(h).TurnKey));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("needs_you")]
    public async Task Saving_a_complete_or_needs_you_summary_settles_it_at_once(string status)
    {
        using var h = await OrchestratedAsync();
        await ArmAsync(h);
        var d = await h.ReadAsync();

        var (code, _) = await h.Summaries.AuthorAsync(d, h.Agent(h.F.AgentId), DiscussionSummaries.Version(d),
            status == "complete" ? Complete : NeedsYouText);

        Assert.Equal(200, code);
        Assert.Equal(status, State(h).Settled);
        Assert.Null(State(h).DueAt);
        Assert.Equal(0, await Sweeper(h).SweepAsync());
        Assert.Empty(HeartbeatNotes(h));
    }

    [Fact]
    public void Settling_follows_the_summary_as_the_owner_sees_it()
    {
        static string? Reason(DiscussionRead d, Func<string, DiscussionRead?>? lookup = null)
            => DiscussionSummaries.SettlingReason(d, DiscussionSummaries.View(d, true, lookup));
        var needsYou = new SummaryText("needs_you", "Launch approval", "Ready to launch.", null, "Approve the Sandbox launch", SummaryText.You);
        var waitingOnB = new SummaryText("waiting", "Waiting on B", "B must finish first.", null, null, "discussion:b");

        Assert.Equal("complete", Reason(Discussion("a", Complete)));
        Assert.Equal("needs_you", Reason(Discussion("a", needsYou)));
        Assert.Null(Reason(Discussion("a", Waiting)));
        Assert.Null(Reason(Discussion("a", Complete, stale: true)));                 // stale: a newer turn decides
        Assert.Null(Reason(Discussion("a", Complete, status: "thinking")));          // running: not settled
        Assert.Null(Reason(Discussion("a", Complete) with { SessionId = "other" })); // another session's summary
        Assert.Null(Reason(Discussion("a", Complete) with { AutoSummaryOverride = false }));

        // Waiting on a discussion of the same owner that needs them resolves to needs_you, and settles.
        var b = Discussion("b", needsYou);
        Assert.Equal("needs_you", Reason(Discussion("a", waitingOnB), id => id == "b" ? b : null));
        Assert.Null(Reason(Discussion("a", waitingOnB), id => id == "b" ? Discussion("b", Waiting) : null));
    }

    [Fact]
    public async Task A_summary_saved_on_the_awaited_discussion_settles_the_one_waiting_on_it()
    {
        // The target waits on Orbit B, another discussion of the same owner.
        using var h = await OrchestratedAsync(owner => [CallbackDeliveryTests.Entity("discussion", DiscussionStore.Slug("orbit-b"), new()
        {
            ["discussion_id"] = "orbit-b", ["app"] = "nova", ["type"] = "chat", ["status"] = "idle", ["title"] = "Orbit B",
            ["agent"] = Guid.NewGuid().ToString(), ["session_id"] = "b-session", ["owner_id"] = owner,
            ["last_activity"] = "2026-11-27T21:00:00Z",
        })]);
        await StoreSummaryAsync(h, new SummaryText("waiting", "Waiting on Orbit B", "Orbit B must land first.", null, null, "discussion:orbit-b"));
        await ArmAsync(h);
        Assert.False(State(h).IsSettled);

        // Orbit B now needs its person: the target, waiting on it, needs them too, and settles straight away.
        var b = (await h.F.Store.GetAsync("orbit-b"))!;
        var (code, _) = await h.Summaries.AuthorAsync(b, h.Person(), DiscussionSummaries.Version(b), NeedsYouText);

        Assert.Equal(200, code);
        Assert.Equal((OrchestrationState.NeedsYou, DiscussionSummaries.SettledBySummary), (State(h).Settled, State(h).SettledBy));
        Assert.Null(State(h).DueAt);
        Assert.Equal(0, await Sweeper(h).SweepAsync());
        Assert.Empty(HeartbeatNotes(h));
    }

    [Fact]
    public async Task The_Heartbeat_settles_it_through_the_route_with_who_and_why()
    {
        using var h = await OrchestratedAsync();
        await ArmAsync(h);
        var d = await h.ReadAsync();

        var result = await DiscussionEndpoints.SettleOrchestrationAsync(d, new("Shipped; only Laurent's review remains."),
            h.Agent(h.F.AgentId), h.F.Entities, h.Events, h.Clock);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
        var state = State(h);
        Assert.Equal(OrchestrationState.ByHeartbeat, state.Settled);
        Assert.Equal($"agent:{h.F.AgentId}", state.SettledBy);
        Assert.Equal("Shipped; only Laurent's review remains.", state.SettledNote);
        Assert.Equal(h.Clock.Now, state.SettledAt);
        Assert.Null(state.DueAt);
        Assert.Contains(h.Events.Invalidations, e => e["change"]?.GetValue<string>() == "orchestration");
        var info = JsonSerializer.SerializeToElement(DiscussionStore.ToInfo(await h.ReadAsync())).GetProperty("orchestration");
        Assert.Equal("settled", info.GetProperty("state").GetString());
        Assert.Equal("heartbeat", info.GetProperty("settledReason").GetString());
        Assert.Equal("Shipped; only Laurent's review remains.", info.GetProperty("settledNote").GetString());

        h.Clock.Now += TimeSpan.FromHours(3);
        Assert.Equal(0, await Sweeper(h).SweepAsync());
        Assert.Empty(HeartbeatNotes(h));

        // Its owner may too, and it records them.
        var byOwner = await DiscussionEndpoints.SettleOrchestrationAsync(await h.ReadAsync(), new("Done."), h.Person(), h.F.Entities, h.Events, h.Clock);
        Assert.Equal(200, ((IStatusCodeHttpResult)byOwner).StatusCode ?? 200);
        Assert.Equal($"person:{h.Owner}", State(h).SettledBy);
    }

    [Fact]
    public async Task A_foreign_caller_cannot_settle_it()
    {
        using var h = await OrchestratedAsync();
        await ArmAsync(h);
        var d = await h.ReadAsync();
        var before = State(h);

        foreach (var foreign in new[] { h.Person(Guid.NewGuid().ToString()), h.Agent(Guid.NewGuid()) })
        {
            var result = await DiscussionEndpoints.SettleOrchestrationAsync(d, new("Settling someone else's work."), foreign, h.F.Entities, h.Events, h.Clock);
            Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        }

        Assert.Equal(before, State(h));
        Assert.DoesNotContain(h.Events.Invalidations, e => e["change"]?.GetValue<string>() == "orchestration");
    }

    [Fact]
    public async Task Settling_needs_a_reason_and_an_Orchestrated_discussion()
    {
        using var h = await OrchestratedAsync();
        var d = await h.ReadAsync();
        var agent = h.Agent(h.F.AgentId);

        foreach (var note in new[] { null, " ", new string('x', DiscussionEndpoints.MaxSettleNote + 1), "two\nlines" })
            Assert.Equal(400, ((IStatusCodeHttpResult)await DiscussionEndpoints.SettleOrchestrationAsync(d, new(note), agent, h.F.Entities, h.Events, h.Clock)).StatusCode);
        var plain = (await h.F.Store.SetOrchestratedAsync(h.F.Target.Id, false))!;
        Assert.Equal(409, ((IStatusCodeHttpResult)await DiscussionEndpoints.SettleOrchestrationAsync(plain, new("Done."), agent, h.F.Entities, h.Events, h.Clock)).StatusCode);
        Assert.False(State(h).IsSettled);
    }
}
