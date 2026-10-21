using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class AgentActivitySignalsTests
{
    private static readonly string AgentId = "6f0b1c1e-6d1f-4d2a-9a51-2f6f2b9d0a11";
    private static readonly string[] PayloadKeys = ["agentId", "phase", "userFacing", "at", "turn"];

    [Fact]
    public async Task Normal_turn_publishes_thinking_speaking_idle_once_each()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(
            Observe("Active", (1, null)),
            Observe("Active", (1, null), (2, AgentActivityPhase.Speaking)),
            Observe("Active", (1, null), (2, AgentActivityPhase.Speaking), (3, AgentActivityPhase.Speaking)),
            Observe("Idle", (1, null), (2, AgentActivityPhase.Speaking), (3, AgentActivityPhase.Speaking)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);

        var published = await bus.WaitForAsync(3);
        Assert.Equal(new[] { "thinking", "speaking", "idle" }, Phases(published));
        Assert.All(published, e => Assert.Equal(AgentId, e["agentId"]!.GetValue<string>()));
        Assert.All(published, e => Assert.True(e["userFacing"]!.GetValue<bool>()));
        Assert.Single(published.Select(e => e["turn"]!.GetValue<string>()).Distinct());
        await AssertNoMoreAsync(bus, 3);
    }

    [Fact]
    public async Task Tool_using_turn_publishes_working_only_on_change()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(
            Observe("Active", (10, null)),
            Observe("Active", (10, null), (11, AgentActivityPhase.Speaking)),
            Observe("Active", (11, AgentActivityPhase.Speaking), (12, AgentActivityPhase.Working)),
            // Tool result after the call: still working, nothing new.
            Observe("Active", (12, AgentActivityPhase.Working), (13, AgentActivityPhase.Working)),
            Observe("Active", (13, AgentActivityPhase.Working), (14, AgentActivityPhase.Speaking)),
            Observe("Idle", (14, AgentActivityPhase.Speaking)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);

        var published = await bus.WaitForAsync(5);
        Assert.Equal(new[] { "thinking", "speaking", "working", "speaking", "idle" }, Phases(published));
        await AssertNoMoreAsync(bus, 5);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Stopped")]
    [InlineData("Cancelled")]
    public async Task Failed_stopped_or_cancelled_turn_ends_idle(string terminal)
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(
            Observe("Active", (1, null)),
            Observe("Active", (1, null), (2, AgentActivityPhase.Working)),
            Observe(terminal, (1, null), (2, AgentActivityPhase.Working)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);

        Assert.Equal(new[] { "thinking", "working", "idle" }, Phases(await bus.WaitForAsync(3)));
        await AssertNoMoreAsync(bus, 3);
    }

    [Fact]
    public async Task Turn_that_never_starts_ends_idle_after_the_grace_period()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Idle", (1, AgentActivityPhase.Speaking))));

        signals.TurnAdmitted(Discussion(), userFacing: true);

        // The earlier turn's reply (record 1) is below the baseline and never counts.
        Assert.Equal(new[] { "thinking", "idle" }, Phases(await bus.WaitForAsync(2)));
    }

    [Fact]
    public async Task Repeated_admissions_and_status_writes_in_one_turn_are_deduplicated()
    {
        var bus = new RecordingEvents();
        var gate = new TaskCompletionSource();
        var probe = new ScriptedProbe(Observe("Active", (1, null)), Observe("Active", (1, null), (2, AgentActivityPhase.Speaking)))
        {
            HoldAfter = 2, Hold = gate.Task,
        };
        using var signals = Signals(bus, probe);
        var discussion = Discussion();

        signals.TurnAdmitted(discussion, userFacing: true);
        signals.TurnAdmitted(discussion, userFacing: true);
        signals.StatusWritten(discussion with { Status = DiscussionStatus.Thinking });
        await bus.WaitForAsync(2);
        signals.TurnAdmitted(discussion, userFacing: false);
        signals.StatusWritten(discussion with { Status = DiscussionStatus.Thinking });
        // A status write while the watcher owns the turn does not end it early.
        signals.StatusWritten(discussion with { Status = DiscussionStatus.Idle });

        await AssertNoMoreAsync(bus, 2);
        Assert.Equal(new[] { "thinking", "speaking" }, Phases(bus.Snapshot()));
        Assert.Equal(1, probe.Watchers);
        gate.SetResult();
    }

    [Fact]
    public async Task Input_admitted_as_a_turn_settles_keeps_the_watch_alive()
    {
        var bus = new RecordingEvents();
        AgentActivitySignals? signals = null;
        var discussion = Discussion();
        var probe = new ScriptedProbe(
            Observe("Active", (1, null)),
            Observe("Idle", (1, null)),
            // Queued input is admitted while the watcher is about to settle.
            Observe("Idle", (1, null)),
            Observe("Active", (1, null), (2, AgentActivityPhase.Speaking)),
            Observe("Idle", (2, AgentActivityPhase.Speaking)))
        {
            OnCall = call => { if (call == 3) signals!.TurnAdmitted(discussion, userFacing: true); },
        };
        using (signals = Signals(bus, probe))
        {
            signals.TurnAdmitted(discussion, userFacing: true);

            Assert.Equal(new[] { "thinking", "speaking", "idle" }, Phases(await bus.WaitForAsync(3)));
            await AssertNoMoreAsync(bus, 3);
        }
    }

    [Fact]
    public async Task Next_turn_gets_a_new_turn_id()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(Observe("Active", (1, null)), Observe("Idle", (1, null)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);
        await bus.WaitForAsync(2);
        probe.Restart();
        signals.TurnAdmitted(Discussion(), userFacing: true);

        var published = await bus.WaitForAsync(4);
        Assert.Equal(new[] { "thinking", "idle", "thinking", "idle" }, Phases(published));
        Assert.NotEqual(published[0]["turn"]!.GetValue<string>(), published[2]["turn"]!.GetValue<string>());
        Assert.Equal(published[2]["turn"]!.GetValue<string>(), published[3]["turn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Heartbeat_discussion_is_never_user_facing()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });

        signals.TurnAdmitted(Discussion(type: HeartbeatService.DiscussionType), userFacing: true);

        Assert.False(Assert.Single(await bus.WaitForAsync(1))["userFacing"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Turn_discovered_by_status_reconciliation_uses_the_discussion_kind()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });

        signals.StatusWritten(Discussion("chat-1") with { Status = DiscussionStatus.Thinking });
        signals.StatusWritten(Discussion("hb-1", type: HeartbeatService.DiscussionType) with { Status = DiscussionStatus.Thinking });

        var published = await bus.WaitForAsync(2);
        Assert.Equal(new[] { true, false }, published.Select(e => e["userFacing"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Person_joining_an_automation_turn_upgrades_it_once()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });
        var discussion = Discussion();

        signals.TurnAdmitted(discussion, userFacing: false);
        await bus.WaitForAsync(1);
        signals.TurnAdmitted(discussion, userFacing: true);
        signals.TurnAdmitted(discussion, userFacing: true);
        signals.TurnAdmitted(discussion, userFacing: false);

        var published = await bus.WaitForAsync(2);
        await AssertNoMoreAsync(bus, 2);
        Assert.Equal(new[] { "thinking", "thinking" }, Phases(published));
        Assert.Equal(new[] { false, true }, published.Select(e => e["userFacing"]!.GetValue<bool>()));
        Assert.Equal(published[0]["turn"]!.GetValue<string>(), published[1]["turn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Confidential_discussion_publishes_nothing()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(Observe("Active", (1, null)), Observe("Idle", (1, null)));
        using var signals = Signals(bus, probe);
        var discussion = Discussion() with { Confidential = true };

        signals.TurnAdmitted(discussion, userFacing: true);
        signals.StatusWritten(discussion with { Status = DiscussionStatus.Thinking });
        signals.StatusWritten(discussion with { Status = DiscussionStatus.Idle });

        await AssertNoMoreAsync(bus, 0);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Turn_that_becomes_confidential_stops_silently_without_idle()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(
            Observe("Active", (1, null)),
            Observe("Active", (1, null), (2, AgentActivityPhase.Speaking)) with { Confidential = true },
            Observe("Idle", (1, null)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);

        Assert.Equal(new[] { "thinking" }, Phases(await bus.WaitForAsync(1)));
        await AssertNoMoreAsync(bus, 1);
    }

    [Fact]
    public async Task Untracked_status_writes_after_restart_publish_nothing()
    {
        var bus = new RecordingEvents();
        var probe = new ScriptedProbe(Observe("Idle"));
        using var signals = Signals(bus, probe);

        // A fresh process knows no turns: reconciliation settling old discussions is silent.
        signals.StatusWritten(Discussion("a") with { Status = DiscussionStatus.Idle });
        signals.StatusWritten(Discussion("b") with { Status = DiscussionStatus.Stopped });
        signals.StatusWritten(Discussion("c") with { Status = DiscussionStatus.Archived });

        await AssertNoMoreAsync(bus, 0);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Discussion_without_a_valid_agent_publishes_nothing()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active")));

        signals.TurnAdmitted(Discussion() with { AgentId = null }, userFacing: true);
        signals.TurnAdmitted(Discussion("x") with { AgentId = "nova" }, userFacing: true);

        await AssertNoMoreAsync(bus, 0);
    }

    [Fact]
    public async Task Throwing_bus_never_reaches_the_caller_and_later_signals_still_publish()
    {
        var bus = new RecordingEvents { ThrowOnPublish = true };
        var probe = new ScriptedProbe(Observe("Active", (1, null)), Observe("Active", (1, null), (2, AgentActivityPhase.Speaking)), Observe("Idle", (2, AgentActivityPhase.Speaking)));
        using var signals = Signals(bus, probe);

        signals.TurnAdmitted(Discussion(), userFacing: true);

        // Each attempt throws inside the pump; all three phases are still attempted in order.
        Assert.Equal(new[] { "thinking", "speaking", "idle" }, Phases(await bus.WaitForAsync(3)));
    }

    [Fact]
    public async Task Blocked_subscriber_never_delays_the_turn_path()
    {
        var release = new TaskCompletionSource();
        var bus = new RecordingEvents { Block = release.Task };
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            signals.TurnAdmitted(Discussion($"d{i}"), userFacing: true);
            signals.StatusWritten(Discussion($"d{i}") with { Status = DiscussionStatus.Thinking });
        }
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"turn path took {clock.Elapsed}");
        await bus.WaitForAsync(1);
        release.SetResult();
        Assert.Equal(50, (await bus.WaitForAsync(50)).Count);
    }

    [Fact]
    public async Task Throwing_probe_is_contained_and_the_turn_still_ends()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ThrowingProbe());

        signals.TurnAdmitted(Discussion(), userFacing: true);

        // No observation means no proof the turn ran; the watch bound ends it.
        Assert.Equal(new[] { "thinking", "idle" }, Phases(await bus.WaitForAsync(2, TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Payloads_carry_no_content_fields()
    {
        var bus = new RecordingEvents();
        var raw = JsonDocument.Parse("""
            {"session":{"id":"s","status":"Active","title":"Secret plans","confidential":false},
             "messages":[
               {"id":1,"role":"user","eventType":"text","content":"SECRET-PROMPT"},
               {"id":2,"role":"assistant","eventType":"tool_use","toolName":"SECRET-TOOL","toolInput":"{\"path\":\"SECRET-ARG\"}"},
               {"id":3,"role":"user","eventType":"tool_result","content":"SECRET-RESULT"},
               {"id":4,"role":"assistant","eventType":"text","content":"SECRET-REPLY"}]}
            """).RootElement;
        var observed = RedComputeAgentActivityProbe.Read(raw)!;
        Assert.Equal(
            new (long, string?)[] { (1, null), (2, "working"), (3, "working"), (4, "speaking") },
            observed.Markers.Select(m => (m.Id, m.Phase)));

        var probe = new ScriptedProbe(
            observed with { Markers = [new(1, null)] },
            observed,
            observed with { Status = "Idle" });
        using var signals = Signals(bus, probe);
        signals.TurnAdmitted(Discussion() with { Title = "SECRET-TITLE" }, userFacing: true);

        var published = await bus.WaitForAsync(3);
        Assert.All(published, e => Assert.Equal(PayloadKeys, e.Select(p => p.Key)));
        var json = string.Join("\n", published.Select(e => e.ToJsonString()));
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("discussion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(bus.Types(), type => Assert.Equal("nova.agent.activity", type));
        Assert.All(published, e => Assert.True(DateTimeOffset.TryParse(e["at"]!.GetValue<string>(), out _)));
    }

    [Fact]
    public void Probe_reads_confidential_session_and_ignores_reasoning_and_blank_text()
    {
        var raw = JsonDocument.Parse("""
            {"session":{"status":"Active","confidential":true},
             "messages":[
               {"id":5,"role":"assistant","eventType":"thinking","content":"x"},
               {"id":6,"role":"assistant","eventType":"text","content":"  "},
               {"id":7,"role":"assistant","eventType":"error","content":"boom"},
               {"role":"assistant","eventType":"text","content":"no id"}]}
            """).RootElement;

        var observed = RedComputeAgentActivityProbe.Read(raw)!;

        Assert.True(observed.Confidential);
        Assert.All(observed.Markers, m => Assert.Null(m.Phase));
        Assert.Equal(new[] { 5L, 6L, 7L }, observed.Markers.Select(m => m.Id));
        Assert.Null(RedComputeAgentActivityProbe.Read(JsonDocument.Parse("{}").RootElement));
    }

    // ── Integration with the existing Nova paths ────────────────────

    [Theory]
    [InlineData("heartbeat-tick")]
    [InlineData("automation:Morning brief")]
    [InlineData("discord-reply")]
    [InlineData("agent-response:s1")]
    public async Task Injected_events_are_not_user_facing(string source)
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });
        using var f = new CallbackDeliveryTests.Fixture(activity: signals);
        var target = (await f.Store.GetAsync("target"))!;

        Assert.True(await f.Injector.InjectAsync(target, "tick", null, source));

        var thinking = Assert.Single(await bus.WaitForAsync(1));
        Assert.Equal("thinking", thinking["phase"]!.GetValue<string>());
        Assert.False(thinking["userFacing"]!.GetValue<bool>());
        Assert.Equal(f.AgentId.ToString("D"), thinking["agentId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delegation_callback_turn_is_not_user_facing()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });
        using var f = new CallbackDeliveryTests.Fixture(activity: signals);

        Assert.Equal(200, await f.Complete("callback-1"));

        Assert.False(Assert.Single(await bus.WaitForAsync(1))["userFacing"]!.GetValue<bool>());
    }

    [Fact]
    public async Task System_events_and_confidential_injections_publish_nothing()
    {
        var bus = new RecordingEvents();
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))));
        using (var f = new CallbackDeliveryTests.Fixture(activity: signals))
        {
            var target = (await f.Store.GetAsync("target"))!;
            await f.Injector.InjectAsync(target, "Natural Voice started", "system", "voice-session");
        }
        using (var f = new CallbackDeliveryTests.Fixture(confidential: true, activity: signals))
        {
            var target = (await f.Store.GetAsync("target"))!;
            Assert.True(await f.Injector.InjectAsync(target, "tick", null, "heartbeat-tick"));
        }

        await AssertNoMoreAsync(bus, 0);
    }

    [Fact]
    public async Task Failing_bus_does_not_affect_callback_delivery()
    {
        var bus = new RecordingEvents { ThrowOnPublish = true };
        using var signals = Signals(bus, new ThrowingProbe());
        using var f = new CallbackDeliveryTests.Fixture(activity: signals);

        Assert.Equal(200, await f.Complete("callback-1"));
        Assert.Single(f.Gateway.Admitted);
        await bus.WaitForAsync(1);
    }

    [Fact]
    public async Task Status_write_point_reports_writes_and_survives_a_failing_bus()
    {
        var bus = new RecordingEvents { ThrowOnPublish = true };
        using var signals = Signals(bus, new ScriptedProbe(Observe("Active", (1, null))) { HoldAfter = 1, Hold = new TaskCompletionSource().Task });
        var store = StatusStore(signals, out var entityId);

        Assert.Equal(DiscussionStatus.Thinking, await store.TrySetStatusAsync(entityId, DiscussionStatus.Thinking));
        // Unchanged status is not a write and is not reported again.
        Assert.Equal(DiscussionStatus.Thinking, await store.TrySetStatusAsync(entityId, DiscussionStatus.Thinking));

        var thinking = Assert.Single(await bus.WaitForAsync(1));
        Assert.True(thinking["userFacing"]!.GetValue<bool>());
        await AssertNoMoreAsync(bus, 1);
    }

    [Theory]
    [InlineData(false, null, null, null, false, true)]
    [InlineData(true, "agent", "user", null, false, false)]
    [InlineData(true, "app", "user", null, true, true)]
    [InlineData(true, "app", "user", null, false, false)]
    [InlineData(true, "app", "user", "parent-1", true, false)]
    [InlineData(true, "user", "user", null, false, true)]
    [InlineData(true, "application", "user", null, true, false)]
    public void Human_initiation_follows_the_signed_execution_actor(
        bool execution, string? actorKind, string? beneficiaryKind, string? parent, bool browser, bool expected)
    {
        var claims = new List<Claim> { new("sub", "owner-1") };
        if (execution)
        {
            claims.Add(new("token_use", "execution"));
            var identity = new JsonObject
            {
                ["app"] = new JsonObject { ["id"] = "float" },
                ["actor"] = new JsonObject { ["kind"] = actorKind, ["id"] = "actor-1" },
                ["beneficiary"] = new JsonObject { ["kind"] = beneficiaryKind, ["id"] = "owner-1" },
                ["context"] = browser
                    ? new JsonArray(new JsonObject { ["kind"] = "browser", ["route"] = "/apps/float" })
                    : new JsonArray(),
                ["parentExecutionId"] = parent,
            };
            claims.Add(new("execution_identity", identity.ToJsonString()));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        Assert.Equal(expected, DiscussionAccessPolicy.IsHumanInitiated(principal));
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static AgentActivitySignals Signals(RecordingEvents bus, IAgentActivityProbe probe)
        => new(bus, probe, pollInterval: TimeSpan.FromMilliseconds(10),
            startGrace: TimeSpan.FromMilliseconds(60), maxWatch: TimeSpan.FromSeconds(2));

    private static DiscussionRead Discussion(string id = "d1", string type = "chat")
        => new(id, "Title", $"session-{id}", DiscussionStatus.Idle, DateTime.UtcNow, DateTime.UtcNow,
            0, null, "owner-1", Guid.NewGuid(), AgentId, type);

    private static AgentActivityObservation Observe(string status, params (long Id, string? Phase)[] markers)
        => new(status, false, markers.Select(m => new AgentActivityMarker(m.Id, m.Phase)).ToList());

    private static string[] Phases(IEnumerable<JsonObject> events)
        => events.Select(e => e["phase"]!.GetValue<string>()).ToArray();

    private static async Task AssertNoMoreAsync(RecordingEvents bus, int expected)
    {
        await Task.Delay(250);
        Assert.Equal(expected, bus.Snapshot().Count);
    }

    private static DiscussionStore StatusStore(AgentActivitySignals signals, out Guid entityId)
    {
        var entity = new LeafEntity(Guid.NewGuid(), "discussion", DiscussionStore.Slug("status-1"), "status-1",
            new JsonObject
            {
                ["discussion_id"] = "status-1", ["app"] = "nova", ["status"] = "idle",
                ["agent"] = AgentId, ["session_id"] = "session-status-1", ["owner_id"] = "owner-1",
            }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
        entityId = entity.Id;
        var current = entity;
        var entities = CallbackDeliveryTests.Proxy.Create<IEntityStore>((method, args) =>
        {
            switch (method)
            {
                case "GetAsync":
                    return Task.FromResult<LeafEntity?>(current);
                case "PatchAsync":
                    var data = current.Data.DeepClone().AsObject();
                    foreach (var (key, value) in (JsonObject)args[1]!) data[key] = value?.DeepClone();
                    current = current with { Data = data };
                    return Task.FromResult<LeafEntity?>(current);
                default:
                    throw new NotSupportedException(method);
            }
        });
        var discussions = CallbackDeliveryTests.Proxy.Create<IDiscussions>((method, _) => throw new NotSupportedException(method));
        return new DiscussionStore(entities, discussions, signals);
    }

    private sealed class RecordingEvents : IPluginEvents
    {
        private readonly ConcurrentQueue<(string Type, JsonObject Payload)> published = new();
        public bool ThrowOnPublish { get; init; }
        public Task? Block { get; init; }

        public async Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
        {
            published.Enqueue((eventType, payload));
            if (Block is not null) await Block;
            if (ThrowOnPublish) throw new InvalidOperationException("subscriber exploded");
        }

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => throw new NotSupportedException();

        public List<JsonObject> Snapshot() => published.Select(p => p.Payload).ToList();
        public List<string> Types() => published.Select(p => p.Type).ToList();

        public async Task<List<JsonObject>> WaitForAsync(int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (published.Count < count && DateTime.UtcNow < deadline) await Task.Delay(5);
            var snapshot = Snapshot();
            Assert.True(snapshot.Count >= count, $"expected {count} events, saw {snapshot.Count}: {string.Join(",", Phases(snapshot))}");
            return snapshot;
        }
    }

    private sealed class ScriptedProbe(params AgentActivityObservation[] script) : IAgentActivityProbe
    {
        private int next;
        private int calls;
        private readonly ConcurrentDictionary<string, byte> sessions = new();
        public int Calls => calls;
        public int Watchers => sessions.Count;
        /// <summary>After this many calls, wait on <see cref="Hold"/> (keeps a turn running).</summary>
        public int HoldAfter { get; init; } = int.MaxValue;
        public Task Hold { get; init; } = Task.CompletedTask;

        public Action<int>? OnCall { get; init; }

        public void Restart() => Interlocked.Exchange(ref next, 0);

        public async Task<AgentActivityObservation?> ProbeAsync(string sessionId, CancellationToken ct)
        {
            sessions.TryAdd(sessionId, 0);
            var call = Interlocked.Increment(ref calls);
            if (call > HoldAfter) await Hold.WaitAsync(ct);
            var index = Interlocked.Increment(ref next) - 1;
            OnCall?.Invoke(call);
            return script[Math.Min(index, script.Length - 1)];
        }
    }

    private sealed class ThrowingProbe : IAgentActivityProbe
    {
        public Task<AgentActivityObservation?> ProbeAsync(string sessionId, CancellationToken ct)
            => throw new HttpRequestException("RedCompute unavailable");
    }
}
