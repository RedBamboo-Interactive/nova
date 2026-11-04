using System.Net;
using System.Text.Json;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 78625958: an event can ask to steer the running turn. RedCompute decides whether it can, and the
/// answer says what became of the request in the shape /delegate uses.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    private const string Steer = "steer-current";
    private const string After = "after-current";
    private const string SteeringItem = """{"id":"q1","state":"pending","delivery":"steer-current"}""";

    private static JsonElement Report(JsonElement body) => body.GetProperty("deliveryReport");

    [Fact]
    public async Task An_event_that_says_nothing_asks_for_nothing()
    {
        using var w = new World("session-1");

        var (status, body) = await w.PostEventAsync("An ordinary note");

        Assert.Equal(200, status);
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        Assert.Null(Assert.Single(w.Gateway.Messages).Delivery);
        Assert.Equal(After, Report(body).GetProperty("requested").GetString());
        Assert.Null(w.EventRecords().Single().Metadata["requested_delivery"]);
    }

    [Fact]
    public async Task A_steer_reaches_redcompute_and_the_answer_reports_what_it_holds()
    {
        using var w = new World("session-1");
        w.Gateway.AdmissionItem = SteeringItem;

        var (status, body) = await w.PostEventAsync("Look at this now", delivery: Steer);

        Assert.Equal(200, status);
        Assert.Equal(Steer, Assert.Single(w.Gateway.Messages).Delivery);
        var report = Report(body);
        Assert.Equal(Steer, report.GetProperty("requested").GetString());
        Assert.Equal(Steer, report.GetProperty("effective").GetString());
        Assert.Equal("pending", report.GetProperty("state").GetString());
        Assert.False(report.GetProperty("effectiveFinal").GetBoolean());
        Assert.Equal("q1", report.GetProperty("queueItemId").GetString());
        // The word callers already read keeps its meaning.
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
    }

    [Fact]
    public async Task A_steer_that_could_not_land_says_it_waits()
    {
        using var w = new World("session-1");
        w.Gateway.AdmissionItem = """{"id":"q1","state":"pending","delivery":"after-current","deliveryReason":"steer_unavailable"}""";

        var (status, body) = await w.PostEventAsync("Look at this now", delivery: Steer);

        Assert.Equal(200, status);
        var report = Report(body);
        Assert.Equal(Steer, report.GetProperty("requested").GetString());
        Assert.Equal(After, report.GetProperty("effective").GetString());
        Assert.Equal("steer_unavailable", report.GetProperty("deliveryReason").GetString());
        Assert.True(report.GetProperty("effectiveFinal").GetBoolean());
    }

    [Theory]
    [InlineData("interrupt-current")]
    [InlineData("now")]
    [InlineData("")]
    [InlineData("STEER-CURRENT")]
    public async Task Anything_else_is_refused_before_anything_is_written(string delivery)
    {
        using var w = new World("session-1");

        var (status, body) = await w.PostEventAsync("Stop everything", delivery: delivery);

        Assert.Equal(400, status);
        Assert.Equal("invalid_delivery", body.GetProperty("error").GetString());
        Assert.Empty(w.Gateway.Messages);
        Assert.Empty(w.EventRecords());
    }

    [Fact]
    public async Task With_no_session_a_steer_is_queued_and_says_so()
    {
        using var w = new World(sessionId: null);

        var (status, body) = await w.PostEventAsync("Waiting for a session", delivery: Steer);

        Assert.Equal(202, status);
        Assert.Equal("queued", body.GetProperty("delivery").GetString());
        var report = Report(body);
        Assert.Equal(Steer, report.GetProperty("requested").GetString());
        Assert.Equal(After, report.GetProperty("effective").GetString());
        Assert.Equal("steer_unavailable", report.GetProperty("deliveryReason").GetString());

        // A default event in the same place has nothing to explain.
        using var plain = new World(sessionId: null);
        var plainReport = Report((await plain.PostEventAsync("Waiting too")).Body);
        Assert.Equal(After, plainReport.GetProperty("effective").GetString());
        Assert.Equal(JsonValueKind.Null, plainReport.GetProperty("deliveryReason").ValueKind);
    }

    [Fact]
    public async Task A_queued_steer_drains_in_order_and_never_overtakes()
    {
        using var w = new World(sessionId: null);
        await w.PostEventAsync("First", delivery: null);
        await w.PostEventAsync("Second, urgent", delivery: Steer);
        await w.PostEventAsync("Third");

        w.Store.SetSession("session-1");
        await w.Injector.DeliverPendingAsync(w.Entity.Id);

        Assert.Equal(new[] { "First", "Second, urgent", "Third" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.All(w.Gateway.Messages, m => Assert.Null(m.Delivery));
    }

    [Fact]
    public async Task A_steer_behind_queued_events_stays_behind_them()
    {
        using var w = new World(sessionId: null);
        await w.PostEventAsync("Earlier");
        w.Store.SetSession("session-1");

        var (status, body) = await w.PostEventAsync("Later, urgent", delivery: Steer);

        Assert.Equal(200, status);
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        Assert.Equal(new[] { "Earlier", "Later, urgent" }, w.Gateway.Messages.Select(m => m.Text));
        Assert.All(w.Gateway.Messages, m => Assert.Null(m.Delivery));
        Assert.Equal("steer_unavailable", Report(body).GetProperty("deliveryReason").GetString());
    }

    [Fact]
    public async Task The_answer_keeps_its_report_when_the_session_being_created_arrives()
    {
        using var w = new World(sessionId: null);
        w.Gateway.CreationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Pipeline.BeginSessionCreation(w.Discussion(), null);
        var posting = w.PostEventAsync("During creation", delivery: Steer);
        await w.WaitUntilAsync(() => w.Queue().Count == 1);

        w.Gateway.CreationGate.SetResult();
        var (status, body) = await posting;

        Assert.Equal(200, status);
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        Assert.Equal("steer_unavailable", Report(body).GetProperty("deliveryReason").GetString());
        Assert.Null(Assert.Single(w.Gateway.Messages).Delivery);
    }

    [Fact]
    public async Task A_keyed_retry_of_a_steer_whose_answer_was_lost_is_the_same_input()
    {
        using var w = new World("session-1");
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;
        Assert.Equal(502, (await w.PostEventAsync("Urgent", idempotencyKey: "k1", delivery: Steer)).Status);

        var (status, body) = await w.PostEventAsync("Urgent", idempotencyKey: "k1", delivery: Steer);

        Assert.Equal(200, status);
        Assert.Equal(Steer, Assert.Single(w.Gateway.Messages).Delivery);
        Assert.Equal(2, w.Gateway.Attempts.Count);
        Assert.Equal(w.Gateway.Attempts[0].Key, w.Gateway.Attempts[1].Key);
        Assert.Single(w.EventRecords());
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
    }

    [Theory]
    [InlineData(null, Steer)]
    [InlineData(Steer, null)]
    [InlineData(Steer, After)]
    public async Task A_keyed_retry_asks_for_what_its_first_attempt_asked_for(string? first, string? retry)
    {
        using var w = new World("session-1");
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;
        await w.PostEventAsync("Urgent", idempotencyKey: "k2", delivery: first);

        var (status, _) = await w.PostEventAsync("Urgent", idempotencyKey: "k2", delivery: retry);

        Assert.Equal(200, status);
        Assert.Equal(first, Assert.Single(w.Gateway.Messages).Delivery);
    }

    [Fact]
    public async Task A_keyed_retry_of_a_steer_the_queue_already_delivered_is_answered_not_refused()
    {
        using var w = new World(sessionId: null);
        Assert.Equal(202, (await w.PostEventAsync("Urgent", idempotencyKey: "k3", delivery: Steer)).Status);
        w.Store.SetSession("session-1");
        await w.Injector.DeliverPendingAsync(w.Entity.Id);
        Assert.Null(Assert.Single(w.Gateway.Messages).Delivery);

        var (status, body) = await w.PostEventAsync("Urgent", idempotencyKey: "k3", delivery: Steer);

        Assert.Equal(200, status);
        Assert.Equal("delivered", body.GetProperty("delivery").GetString());
        var report = Report(body);
        Assert.Equal(After, report.GetProperty("effective").GetString());
        Assert.Equal("steer_unavailable", report.GetProperty("deliveryReason").GetString());
        Assert.Single(w.Gateway.Messages);
    }

    [Fact]
    public async Task An_uncertain_steer_drained_later_is_still_one_event()
    {
        // The direct steer was admitted but its answer lost; the retry finds events queued, so the
        // drain sends it as after-current under the same key. RedCompute already holds it.
        using var w = new World("session-1");
        w.Gateway.FailNextMessages = 1;
        w.Gateway.AdmitBeforeFailure = true;
        await w.PostEventAsync("Urgent", idempotencyKey: "k4", delivery: Steer);
        w.Store.SetSession(null!);
        Assert.Equal(202, (await w.PostEventAsync("Urgent", idempotencyKey: "k4", delivery: Steer)).Status);
        w.Store.SetSession("session-1");

        await w.Injector.DeliverPendingAsync(w.Entity.Id);

        Assert.Equal(Steer, Assert.Single(w.Gateway.Messages).Delivery);
        Assert.Empty(w.Queue());
    }

    [Fact]
    public async Task A_system_event_is_recorded_and_never_forwarded_whatever_it_asks()
    {
        using var w = new World("session-1");

        var (status, body) = await w.PostEventAsync("For display", type: "system", delivery: Steer);

        Assert.Equal(200, status);
        Assert.Equal("recorded", body.GetProperty("delivery").GetString());
        Assert.Empty(w.Gateway.Messages);
        Assert.Equal(JsonValueKind.Null, Report(body).GetProperty("effective").ValueKind);
    }

    [Fact]
    public async Task The_steer_button_reaches_the_item_through_the_discussions_session()
    {
        using var w = new World("session-1");
        var services = new ServiceCollection();
        services.AddSingleton(w.Agents);
        services.AddKeyedSingleton<IEntityStore>(NovaAppPlugin.PluginId, w.Store);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        await DiscussionEndpoints.SteerQueuedInputAsync(w.Discussion().Id, "q/1", ctx, w.Discussions, new RedComputeClient(w.Gateway));

        Assert.Contains("/ai-session/sessions/session-1/input-queue/q%2F1/steer", w.Gateway.Paths);
    }

    [Fact]
    public void The_steer_route_sits_beside_the_other_queue_routes()
    {
        // The route table needs the whole host; the handler above is what it points at.
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Leaf.Plugins.Nova"))) root = Path.GetDirectoryName(root)!;
        var source = File.ReadAllText(Path.Combine(root, "src", "Leaf.Plugins.Nova", "Endpoints", "DiscussionEndpoints.cs"));

        Assert.Contains("""group.MapPost("/discussions/{id}/input-queue/{itemId}/steer", SteerQueuedInputAsync)""", source);
    }

    [Fact]
    public async Task The_steer_button_has_nothing_to_reach_before_a_session_exists()
    {
        using var w = new World(sessionId: null);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        var result = await DiscussionEndpoints.SteerQueuedInputAsync(w.Discussion().Id, "q1", ctx, w.Discussions, new RedComputeClient(w.Gateway));

        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(w.Gateway.Paths);
    }
}
