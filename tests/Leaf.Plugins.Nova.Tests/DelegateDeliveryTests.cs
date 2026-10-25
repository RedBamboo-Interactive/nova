using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// A continuation chooses how its prompt meets the session's running turn, with RedCompute's own
// delivery vocabulary: after-current (queue), steer-current, interrupt-current. Nova forwards it
// verbatim, reports only what RedCompute's admission says happened, and binds the completion
// callback to this prompt's own messageUid in every mode.
public sealed class DelegateDeliveryTests
{
    public static TheoryData<string?> Modes => new() { null, "after-current", "steer-current", "interrupt-current" };

    [Theory]
    [InlineData("queue")]
    [InlineData("steer")]
    [InlineData("interrupt")]
    [InlineData("Interrupt-Current")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Unknown_delivery_is_refused_before_anything_is_sent(string delivery)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", Delivery = delivery });

        Assert.Equal(400, status);
        Assert.Equal("invalid_delivery", body.GetProperty("error").GetString());
        Assert.Empty(compute.Requests);
        Assert.Empty(f.TargetMessages);
    }

    [Theory]
    [InlineData("steer-current")]
    [InlineData("interrupt-current")]
    public async Task A_new_session_refuses_a_mode_that_needs_a_running_turn(string delivery)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (status, body) = await Delegate(f, compute, new() { Repository = "nova", Prompt = "work", Delivery = delivery });

        Assert.Equal(400, status);
        Assert.Equal("delivery_requires_session", body.GetProperty("error").GetString());
        Assert.Empty(compute.Requests);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("after-current", false, true)]
    [InlineData("after-current", true, true)]
    [InlineData("steer-current", true, true)]
    [InlineData("interrupt-current", true, true)]
    [InlineData("steer-current", false, false)]
    [InlineData("interrupt-current", false, false)]
    public void Delivery_rule(string? delivery, bool continuation, bool accepted)
        => Assert.Equal(accepted, DelegateEndpoints.DeliveryError(delivery, continuation) is null);

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Each_mode_is_forwarded_verbatim_with_the_prompt_identity(string? delivery)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop touching that repo", DiscussionId = "target", Delivery = delivery });

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("promptSent").GetBoolean());
        var message = Assert.Single(compute.Messages);
        Assert.Equal(delivery ?? "after-current", message.Body.GetProperty("delivery").GetString());
        Assert.Equal("stop touching that repo", message.Body.GetProperty("content").GetString());
        var uid = message.Body.GetProperty("messageUid").GetString();
        Assert.Equal($"nova-delegate:worker:{uid}", message.IdempotencyKey);
        Assert.Equal(delivery ?? "after-current", body.GetProperty("delivery").GetProperty("requested").GetString());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Every_mode_binds_its_callback_to_this_prompt(string? delivery)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (_, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "next", DiscussionId = "target", Delivery = delivery });

        Assert.True(body.GetProperty("callbackRegistered").GetBoolean());
        var uid = Assert.Single(compute.Messages).Body.GetProperty("messageUid").GetString();
        var callbacks = compute.Requests.Where(r => r.Path.EndsWith("/callback")).ToArray();
        // Subscribed before dispatch (forced) and confirmed after admission, both for this exact prompt.
        Assert.Equal(2, callbacks.Length);
        Assert.True(callbacks[0].Body.GetProperty("force").GetBoolean());
        Assert.True(compute.Requests.IndexOf(callbacks[0]) < compute.Requests.IndexOf(compute.Messages[0]));
        Assert.All(callbacks, c =>
        {
            Assert.Equal(uid, c.Body.GetProperty("callbackId").GetString());
            Assert.Equal(uid, c.Body.GetProperty("promptMessageUid").GetString());
            Assert.Contains("discussionId=target", c.Body.GetProperty("questionUrl").GetString());
        });
    }

    [Fact]
    public async Task Queued_behind_a_running_turn_reports_the_inputs_ahead()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("after-current", "pending", head: "q_earlier") };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "next" })).Body.GetProperty("delivery");

        Assert.Equal("after-current", delivery.GetProperty("effective").GetString());
        Assert.True(delivery.GetProperty("effectiveFinal").GetBoolean());
        Assert.Equal("pending", delivery.GetProperty("state").GetString());
        Assert.False(delivery.GetProperty("turnInterrupted").GetBoolean());
        Assert.True(delivery.GetProperty("inputsAhead").GetBoolean());
        Assert.Equal("q_item", delivery.GetProperty("queueItemId").GetString());
        Assert.False(delivery.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Steer_accepted_into_the_running_turn()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("steer-current", "delivered") };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "also check X", Delivery = "steer-current" })).Body.GetProperty("delivery");

        Assert.Equal("steer-current", delivery.GetProperty("requested").GetString());
        Assert.Equal("steer-current", delivery.GetProperty("effective").GetString());
        Assert.True(delivery.GetProperty("effectiveFinal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, delivery.GetProperty("demotionReason").ValueKind);
        Assert.False(delivery.GetProperty("turnInterrupted").GetBoolean());
        Assert.False(delivery.GetProperty("inputsAhead").GetBoolean());
    }

    [Fact]
    public async Task Steer_demoted_by_RedCompute_is_reported_as_queued_never_as_steered()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("after-current", "pending", head: "q_item", errorCode: "steer_unavailable") };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "also check X", Delivery = "steer-current" })).Body.GetProperty("delivery");

        Assert.Equal("steer-current", delivery.GetProperty("requested").GetString());
        Assert.Equal("after-current", delivery.GetProperty("effective").GetString());
        Assert.Equal("steer_unavailable", delivery.GetProperty("demotionReason").GetString());
        Assert.True(delivery.GetProperty("effectiveFinal").GetBoolean());
        Assert.False(delivery.GetProperty("inputsAhead").GetBoolean());
    }

    [Fact]
    public async Task Steer_still_pending_is_not_reported_final()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("steer-current", "pending", head: "q_earlier") };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "also check X", Delivery = "steer-current" })).Body.GetProperty("delivery");

        Assert.Equal("steer-current", delivery.GetProperty("effective").GetString());
        Assert.False(delivery.GetProperty("effectiveFinal").GetBoolean());
        // A pending steer overtakes ordinary queued input, so the queue head cannot decide this.
        Assert.Equal(JsonValueKind.Null, delivery.GetProperty("inputsAhead").ValueKind);
    }

    [Theory]
    [InlineData("q_item", false)]
    [InlineData("q_earlier", true)]
    public async Task Interrupt_reports_the_interruption_as_unknown_and_any_input_that_runs_first(string head, bool ahead)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("interrupt-current", "pending", head: head) };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", Delivery = "interrupt-current" })).Body.GetProperty("delivery");

        Assert.Equal("interrupt-current", delivery.GetProperty("effective").GetString());
        // An older RedCompute does not say whether a turn was running to interrupt.
        Assert.Equal(JsonValueKind.Null, delivery.GetProperty("turnInterrupted").ValueKind);
        Assert.Equal(ahead, delivery.GetProperty("inputsAhead").GetBoolean());
    }

    [Fact]
    public async Task Legacy_acceptance_without_a_queue_item_reports_no_effective_mode()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = """{"sent":true}""" };

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", Delivery = "interrupt-current" });

        Assert.Equal(200, status);
        var delivery = body.GetProperty("delivery");
        Assert.Equal("interrupt-current", delivery.GetProperty("requested").GetString());
        Assert.Equal(JsonValueKind.Null, delivery.GetProperty("effective").ValueKind);
        Assert.False(delivery.GetProperty("effectiveFinal").GetBoolean());
    }

    [Theory]
    [InlineData("steer-current")]
    [InlineData("interrupt-current")]
    public async Task A_lost_acknowledgement_is_retried_as_one_replay_never_a_second_steer_or_interrupt(string delivery)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { LoseFirstAcknowledgement = true, Admission = Admission(delivery, "pending", head: "q_item") };

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", DiscussionId = "target", Delivery = delivery });

        Assert.Equal(200, status);
        Assert.Equal(2, compute.Messages.Count);
        Assert.Single(compute.Messages.Select(m => m.IdempotencyKey).Distinct());
        Assert.Single(compute.Messages.Select(m => m.Raw).Distinct());
        Assert.Equal(1, compute.FreshAdmissions);
        Assert.True(body.GetProperty("delivery").GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task The_discussion_marker_records_a_requested_interrupt()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = Admission("interrupt-current", "pending", head: "q_item") };

        await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", DiscussionId = "target", Delivery = "interrupt-current" });

        var marker = Assert.Single(f.TargetMessages);
        Assert.Equal("Delegated to Code session worker (interrupt-current requested)", marker.Content);
        var parts = JsonSerializer.Deserialize<JsonElement>(marker.Metadata["parts_json"]!.GetValue<string>());
        var recorded = parts.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "event_data")
            .GetProperty("data").GetProperty("delivery");
        Assert.Equal("interrupt-current", recorded.GetProperty("requested").GetString());
        Assert.Equal("q_item", recorded.GetProperty("queueItemId").GetString());
    }

    [Fact]
    public async Task A_queued_delegation_keeps_its_plain_marker()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        await Delegate(f, new Compute(), new() { SessionId = "worker", Prompt = "next", DiscussionId = "target" });

        Assert.Equal("Delegated to Code session worker", Assert.Single(f.TargetMessages).Content);
    }

    // The current RedCompute reply: the item names what was requested and why it changed, and an
    // interrupt-current admission says what the interrupt did.
    private static string CurrentAdmission(string requested, string delivery, string state, string? head = null,
        string? deliveryReason = null, string? interruptResult = null)
        => JsonSerializer.Serialize(new
        {
            accepted = true,
            existing = false,
            disposition = state == "delivered" ? "delivered" : "queued",
            queueItemId = "q_item",
            queue = new { depth = 1, state = "waiting_for_session", headItemId = head },
            item = new { id = "q_item", state, delivery, requestedDelivery = requested, deliveryReason, interruptResult },
            interruptResult,
            turnInterrupted = interruptResult == "interrupted",
        });

    [Theory]
    [InlineData("interrupted", true)]
    [InlineData("not_active", false)]
    [InlineData("maintenance", false)]
    public async Task Interrupt_reports_what_RedCompute_says_the_interrupt_did(string result, bool interrupted)
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = CurrentAdmission("interrupt-current", "interrupt-current", "pending", head: "q_item", interruptResult: result) };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "stop", Delivery = "interrupt-current" })).Body.GetProperty("delivery");

        Assert.Equal(interrupted, delivery.GetProperty("turnInterrupted").GetBoolean());
        Assert.Equal(result, delivery.GetProperty("interruptResult").GetString());
        // It overtakes the queue: nothing runs before it.
        Assert.False(delivery.GetProperty("inputsAhead").GetBoolean());
    }

    [Fact]
    public async Task Steer_on_an_idle_session_reports_it_ran_as_the_next_turn()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { Admission = CurrentAdmission("steer-current", "after-current", "delivered", deliveryReason: "no_active_turn") };

        var delivery = (await Delegate(f, compute, new() { SessionId = "worker", Prompt = "also", Delivery = "steer-current" })).Body.GetProperty("delivery");

        Assert.Equal("steer-current", delivery.GetProperty("requested").GetString());
        Assert.Equal("after-current", delivery.GetProperty("effective").GetString());
        Assert.Equal("no_active_turn", delivery.GetProperty("deliveryReason").GetString());
        Assert.Equal("no_active_turn", delivery.GetProperty("demotionReason").GetString());
        Assert.True(delivery.GetProperty("effectiveFinal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, delivery.GetProperty("interruptResult").ValueKind);
    }

    [Fact]
    public async Task A_delegation_binds_the_discussion_session_as_supervisor_before_the_prompt()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "build it", DiscussionId = "target" });

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("supervisorBound").GetBoolean());
        var binding = Assert.Single(compute.Requests, r => r.Path.EndsWith("/supervisor"));
        Assert.Equal("PUT", binding.Method);
        Assert.Equal("/ai-session/sessions/worker/supervisor", binding.Path);
        Assert.Equal("target-session", binding.Body.GetProperty("supervisorSessionId").GetString());
        Assert.True(compute.Requests.IndexOf(binding) < compute.Requests.IndexOf(compute.Messages[0]));
    }

    [Fact]
    public async Task A_delegation_without_a_discussion_binds_no_supervisor()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute();

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "build it" });

        Assert.Equal(200, status);
        Assert.False(body.GetProperty("supervisorBound").GetBoolean());
        Assert.DoesNotContain(compute.Requests, r => r.Path.EndsWith("/supervisor"));
    }

    [Fact]
    public async Task A_refused_supervisor_binding_never_blocks_the_delegation()
    {
        using var f = new CallbackDeliveryTests.Fixture();
        var compute = new Compute { SupervisorAccepted = false };

        var (status, body) = await Delegate(f, compute, new() { SessionId = "worker", Prompt = "build it", DiscussionId = "target" });

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("promptSent").GetBoolean());
        Assert.False(body.GetProperty("supervisorBound").GetBoolean());
    }

    private static string Admission(string delivery, string state, string? head = null, string? errorCode = null, bool existing = false)
        => JsonSerializer.Serialize(new
        {
            accepted = true,
            existing,
            disposition = state == "delivered" ? "delivered" : "queued",
            queueItemId = "q_item",
            queue = new { depth = 1, state = "waiting_for_session", headItemId = head },
            item = new
            {
                id = "q_item",
                state,
                delivery,
                error = errorCode is null ? null : new { code = errorCode, message = "Steering was unavailable", retryable = false },
            },
        });

    private static async Task<(int Status, JsonElement Body)> Delegate(
        CallbackDeliveryTests.Fixture f, Compute compute, DelegateRequest request)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-owner")], "test")),
        };
        var result = await DelegateEndpoints.HandleDelegateAsync(ctx, request, new RedComputeClient(compute),
            f.Agents, null!, f.Store, f.Entities, f.Injector);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var value = ((IValueHttpResult)result).Value;
        return (status, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal sealed record Sent(string Method, string Path, string Raw, JsonElement Body, string? IdempotencyKey);

    /// <summary>RedCompute's session surface as the delegate endpoint uses it.</summary>
    internal sealed class Compute : IComputeGateway
    {
        public List<Sent> Requests { get; } = [];
        public List<Sent> Messages => Requests.Where(r => r.Path.EndsWith("/message")).ToList();
        public string Admission { get; set; } = """{"accepted":true}""";
        public bool LoseFirstAcknowledgement { get; set; }
        public bool SupervisorAccepted { get; set; } = true;
        public int FreshAdmissions { get; private set; }
        private readonly HashSet<string> admitted = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var key = request.Headers.TryGetValues("X-Idempotency-Key", out var values) ? values.Single() : null;
            var path = request.RequestUri!.ToString();
            var sent = new Sent(request.Method.Method, path, raw,
                raw.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(raw), key);
            Requests.Add(sent);

            if (request.Method == HttpMethod.Get)
                return Json("""{"session":{"id":"worker","status":"Active"}}""");
            if (path.EndsWith("/callback"))
                return Json("""{"registered":true}""");
            if (path.EndsWith("/supervisor"))
            {
                Assert.NotNull(provenance);
                return SupervisorAccepted ? Json(raw) : new(HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/message"))
            {
                Assert.NotNull(provenance);
                // RedCompute acts (interrupts, steers) only on a fresh admission; a reused key replays.
                var replay = key is not null && !admitted.Add(key);
                if (!replay) FreshAdmissions++;
                if (LoseFirstAcknowledgement && Messages.Count == 1)
                    throw new HttpRequestException("Simulated lost acknowledgement");
                return Json(replay ? Admission.Replace("\"existing\":false", "\"existing\":true") : Admission);
            }
            throw new InvalidOperationException($"Unexpected RedCompute call {request.Method} {path}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
