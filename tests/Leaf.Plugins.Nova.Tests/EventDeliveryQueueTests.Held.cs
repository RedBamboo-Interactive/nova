using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 2026-11-25 (f0f57b1c audit): an event its sender holds for the next turn (delivery with-next) is
/// stored and shown, starts no turn, and joins the session's next turn once, as its own steer.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    private const string WithNext = "with-next";

    private static List<PendingEventDelivery> Held(World w) => HeldEventDeliveries.Read(w.Entity.Data);

    [Fact]
    public async Task A_held_event_is_shown_and_starts_nothing()
    {
        using var w = new World("session-1");

        var (status, body) = await w.PostEventAsync("FYI: the Nightly is published", delivery: WithNext);

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("held", body.GetProperty("delivery").GetString());
        Assert.Equal(WithNext, Report(body).GetProperty("effective").GetString());
        Assert.Empty(w.Gateway.Messages);
        Assert.Single(w.EventRecords());
        Assert.Single(Held(w));
        Assert.Empty(w.Queue());
    }

    [Fact]
    public async Task A_held_event_joins_the_next_turn_once_as_its_own_steer()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("FYI", delivery: WithNext);
        var heldUid = w.EventRecords().Single().Metadata["uid"]!.GetValue<string>();

        await w.PostEventAsync("Please do this");
        await w.PostEventAsync("And this");

        Assert.Equal(new[] { "Please do this", "FYI", "And this" }, w.Gateway.Messages.Select(m => m.Text));
        var released = w.Gateway.Messages[1];
        Assert.Equal(Steer, released.Delivery);
        Assert.Equal(false, released.WaitForDelivery);
        // Its own identity, not folded into the input that released it.
        Assert.Equal(heldUid, released.MessageUid);
        Assert.Null(w.Gateway.Messages[0].Delivery);
        Assert.Empty(Held(w));
    }

    [Fact]
    public async Task A_person_sending_releases_what_was_held_after_their_message()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("One", delivery: WithNext);
        await w.PostEventAsync("Two", delivery: WithNext);

        var outcome = await w.SendAsync("Hello");

        Assert.True(outcome.Success);
        var texts = w.Gateway.Messages.Select(m => m.Text).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Contains("Hello", w.Gateway.Messages[0].Content);
        Assert.Equal(new[] { "One", "Two" }, texts.Skip(1));
        Assert.All(w.Gateway.Messages.Skip(1), m => Assert.Equal(Steer, m.Delivery));
        Assert.Empty(Held(w));
    }

    [Fact]
    public async Task An_explicit_steer_still_steers_and_brings_the_held_event_along()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("FYI", delivery: WithNext);

        await w.PostEventAsync("Stop, wrong checkout", delivery: Steer);

        Assert.Equal(new[] { "Stop, wrong checkout", "FYI" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.All(w.Gateway.Messages, m => Assert.Equal(Steer, m.Delivery));
    }

    [Fact]
    public async Task Without_a_session_a_held_event_stays_held_and_never_enters_the_queue()
    {
        using var w = new World(sessionId: null);

        var (status, body) = await w.PostEventAsync("FYI", delivery: WithNext);

        Assert.Equal(200, status);
        Assert.Equal("held", body.GetProperty("delivery").GetString());
        Assert.Empty(w.Queue());
        Assert.Single(Held(w));
        Assert.Empty(w.Gateway.Messages);
    }

    [Fact]
    public async Task Held_events_survive_a_restart_and_go_in_order()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("First", delivery: WithNext);
        await w.PostEventAsync("Second", delivery: WithNext);

        using var restarted = w.Restart();
        await restarted.PostEventAsync("Next turn");

        Assert.Equal(new[] { "Next turn", "First", "Second" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.Empty(Held(restarted));
    }

    [Fact]
    public async Task A_release_whose_answer_was_lost_is_never_admitted_twice()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("FYI", delivery: WithNext);
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;

        Assert.Equal(0, await w.Injector.ReleaseHeldAsync(w.Entity.Id));
        Assert.Single(Held(w));

        await w.PostEventAsync("Next turn");

        Assert.Equal(new[] { "FYI", "Next turn" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.Empty(Held(w));
    }

    [Fact]
    public async Task A_held_event_nobody_needed_for_a_day_leaves_without_a_notice()
    {
        using var w = new World("session-1");
        var now = DateTimeOffset.UtcNow;
        w.Injector.Clock = () => now;
        await w.PostEventAsync("FYI", delivery: WithNext);

        w.Injector.Clock = () => now + HeldEventDeliveries.MaxAge + TimeSpan.FromMinutes(1);
        await w.PostEventAsync("Next turn");

        Assert.Equal(new[] { "Next turn" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.Empty(Held(w));
        Assert.Empty(w.Notices("event-undeliverable"));
    }

    [Fact]
    public async Task History_shows_a_held_event_as_held_until_its_copy_is_in_the_transcript()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("FYI", delivery: WithNext);
        var record = w.EventRecords().Single();
        var uid = record.Metadata["uid"]!.GetValue<string>();
        var none = new HashSet<string>();

        var held = DiscussionEndpoints.ProjectHistoryOverlays([record], none, none, sessionBacked: true,
            allowUserBridges: true, heldUids: w.Discussion().HeldEventUids).Single();
        Assert.True(held.Held);

        // Released: the entry is gone, and its copy reached the transcript.
        await w.PostEventAsync("Next turn");
        Assert.Empty(w.Discussion().HeldEventUids!);
        var joined = DiscussionEndpoints.ProjectHistoryOverlays([record], new HashSet<string> { uid }, none,
            sessionBacked: true, allowUserBridges: true, heldUids: [uid]).Single();
        Assert.Null(joined.Held);
    }

    [Fact]
    public async Task Clients_hear_that_an_event_is_held_and_when_it_joins_a_turn()
    {
        using var w = new World("session-1");
        await w.PostEventAsync("FYI", delivery: WithNext);
        await w.PostEventAsync("Plain");
        var uid = w.EventRecords().First().Metadata["uid"]!.GetValue<string>();

        List<(string Name, System.Text.Json.Nodes.JsonObject? Data)> published;
        lock (w.Published) published = [.. w.Published];
        var arrivals = published.Where(p => p.Name == "discussion.event").ToList();
        Assert.True(arrivals[0].Data!["held"]!.GetValue<bool>());
        Assert.Null(arrivals[1].Data!["held"]);
        var released = Assert.Single(published, p => p.Name == "discussion.event-released");
        Assert.Equal(uid, released.Data!["messageUid"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_held_redelivery_under_one_key_is_held_once()
    {
        using var w = new World("session-1");

        await w.PostEventAsync("FYI", idempotencyKey: "fyi-1", delivery: WithNext);
        var (status, body) = await w.PostEventAsync("FYI", idempotencyKey: "fyi-1", delivery: WithNext);

        Assert.Equal(200, status);
        Assert.Equal("held", body.GetProperty("delivery").GetString());
        Assert.Single(w.EventRecords());
        Assert.Single(Held(w));
        await w.PostEventAsync("Next turn");
        Assert.Equal(new[] { "Next turn", "FYI" }, w.Gateway.Messages.Select(m => m.Text));
    }
}
