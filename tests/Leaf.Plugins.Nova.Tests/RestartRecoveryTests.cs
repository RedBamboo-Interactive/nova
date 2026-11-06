using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Probe = Leaf.Plugins.Nova.RedComputeClient.SessionProbe;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// A discussion that ran a rebuild had its own turn killed by the restart and nothing woke it
/// (nine cases since 28 Oct; 6 Nov: d0534d08, session 3f02ef9566b2, last record 12:19:46Z,
/// RedLeaf back 12:19:50Z, still `thinking` at 12:55Z).
/// </summary>
public sealed class RestartRecoveryTests
{
    private static readonly DateTimeOffset Boot = new(2026, 11, 6, 12, 19, 50, TimeSpan.Zero);
    private static readonly DateTimeOffset LastRecord = new(2026, 11, 6, 12, 19, 46, TimeSpan.Zero);

    // ---- The qualification table ----

    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public void Thinking_and_a_restart_stop_and_recent_activity_resumes(string stopReason)
    {
        Assert.Equal(RestartRecoveryVerdict.Resume, Qualify(probe: Stopped(stopReason)));
    }

    [Theory]
    [InlineData("user_stopped")]
    [InlineData("usage_limit")]
    [InlineData("dismissed")]
    [InlineData("idle_timeout")]
    [InlineData("idle_ttl_expired")]
    [InlineData(null)]
    public void A_stop_a_restart_does_not_explain_never_wakes(string? stopReason)
    {
        Assert.Equal(RestartRecoveryVerdict.NotARestartStop, Qualify(probe: Stopped(stopReason)));
    }

    [Fact]
    public void A_crashed_session_is_not_a_restart_stop()
    {
        Assert.Equal(RestartRecoveryVerdict.NotARestartStop,
            Qualify(probe: new Probe(true, "Error", "process_exited", "p")));
    }

    [Theory]
    [InlineData(DiscussionStatus.Idle)]
    [InlineData(DiscussionStatus.Stopped)]
    public void Not_thinking_at_boot_means_no_turn_was_in_flight(string statusAtBoot)
    {
        Assert.Equal(RestartRecoveryVerdict.NotInFlight, Qualify(statusAtBoot: statusAtBoot));
    }

    [Theory]
    [InlineData(DiscussionStatus.Archived)]
    [InlineData(DiscussionStatus.Archiving)]
    public void An_archived_discussion_never_wakes(string status)
    {
        Assert.Equal(RestartRecoveryVerdict.Closed, Qualify(current: Discussion(status: status)));
    }

    [Theory]
    [InlineData("live")]
    [InlineData(HeartbeatService.DiscussionType)]
    public void Live_and_heartbeat_stay_with_the_presence_path(string type)
    {
        Assert.Equal(RestartRecoveryVerdict.Presence, Qualify(current: Discussion(type: type)));
    }

    [Fact]
    public void A_discussion_another_plugin_owns_is_left_to_it()
    {
        Assert.Equal(RestartRecoveryVerdict.Foreign,
            Qualify(current: Discussion() with { OwnerPlugin = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public void A_session_replaced_since_boot_or_gone_is_not_woken()
    {
        Assert.Equal(RestartRecoveryVerdict.NoSession, Qualify(current: Discussion() with { SessionId = "other" }));
        Assert.Equal(RestartRecoveryVerdict.NoSession, Qualify(current: Discussion() with { SessionId = null }));
        Assert.Equal(RestartRecoveryVerdict.NoSession, Qualify(probe: new Probe(true, null)));
    }

    [Fact]
    public void Unreachable_compute_and_a_running_session_are_asked_again()
    {
        Assert.Equal(RestartRecoveryVerdict.ComputeUnreachable, Qualify(probe: new Probe(false, null)));
        Assert.Equal(RestartRecoveryVerdict.StillRunning, Qualify(probe: new Probe(true, "Active", "completed", "p")));
        Assert.Equal(RestartRecoveryVerdict.StillRunning, Qualify(probe: new Probe(true, "Starting", null, "p")));
    }

    [Fact]
    public void An_idle_session_already_finished_or_was_resumed()
    {
        Assert.Equal(RestartRecoveryVerdict.Settled, Qualify(probe: new Probe(true, "Idle", "completed", "p")));
    }

    [Fact]
    public void A_turn_last_seen_long_before_boot_is_stale()
    {
        // The PC was off overnight: the morning boot does not resume last night's turn on its own.
        Assert.Equal(RestartRecoveryVerdict.Stale, Qualify(lastSeen: Boot - TimeSpan.FromMinutes(31)));
        Assert.Equal(RestartRecoveryVerdict.Resume, Qualify(lastSeen: Boot - TimeSpan.FromMinutes(29)));
        Assert.Equal(RestartRecoveryVerdict.Stale, Qualify(lastSeen: null, noLastSeen: true));
    }

    // ---- The persisted guard ----

    [Fact]
    public void First_wake_is_claimed()
    {
        Assert.Equal(RestartRecoveryClaim.Claimed, RestartRecoveryPolicy.Decide(null, "k1", Boot));
    }

    [Fact]
    public void The_same_interruption_is_never_woken_twice()
    {
        // Double boot: the second boot computes the same key, even past the window.
        var record = Record("k1", Boot);
        Assert.Equal(RestartRecoveryClaim.AlreadyWoken, RestartRecoveryPolicy.Decide(record, "k1", Boot.AddMinutes(2)));
        Assert.Equal(RestartRecoveryClaim.AlreadyWoken, RestartRecoveryPolicy.Decide(record, "k1", Boot.AddHours(5)));
    }

    [Fact]
    public void A_second_interruption_within_30_minutes_is_not_woken()
    {
        // The resumed turn rebuilt again and orphaned itself again.
        var record = Record("k1", Boot);
        Assert.Equal(RestartRecoveryClaim.RateLimited, RestartRecoveryPolicy.Decide(record, "k2", Boot.AddMinutes(29)));
        Assert.Equal(RestartRecoveryClaim.Claimed, RestartRecoveryPolicy.Decide(record, "k2", Boot.AddMinutes(30)));
    }

    [Fact]
    public void A_malformed_record_does_not_block_recovery()
    {
        Assert.Equal(RestartRecoveryClaim.Claimed, RestartRecoveryPolicy.Decide(JsonValue.Create("x"), "k1", Boot));
        Assert.Equal(RestartRecoveryClaim.Claimed, RestartRecoveryPolicy.Decide(new JsonObject { ["at"] = "nope" }, "k1", Boot));
    }

    [Fact]
    public void The_idempotency_key_is_discussion_session_and_interruption()
    {
        var key = RestartRecoveryPolicy.Key("d0534d08", "3f02ef9566b2", LastRecord);
        Assert.Equal("restart-recovery:d0534d08:3f02ef9566b2:2026-11-06T12:19:46.0000000Z", key);
        Assert.Equal(key, RestartRecoveryPolicy.Key("d0534d08", "3f02ef9566b2", LastRecord.ToOffset(TimeSpan.FromHours(1))));
        Assert.NotEqual(key, RestartRecoveryPolicy.Key("d0534d08", "3f02ef9566b2", LastRecord.AddSeconds(1)));
        Assert.NotEqual(key, RestartRecoveryPolicy.Key("d0534d08", "other", LastRecord));
    }

    [Fact]
    public async Task The_store_persists_the_claim_on_the_discussion()
    {
        var entities = new ReactionContextTests.Entities();
        var store = new DiscussionStore(entities, new ReactionContextTests.Discussions(entities.DiscussionId));

        Assert.Equal(RestartRecoveryClaim.Claimed, await store.TryClaimRestartRecoveryAsync(entities.DiscussionId, "k1", Boot));
        var saved = entities.Get(entities.DiscussionId).Data[RestartRecoveryPolicy.DataKey]!;
        Assert.Equal("k1", saved["key"]!.GetValue<string>());
        Assert.Equal(RestartRecoveryClaim.AlreadyWoken, await store.TryClaimRestartRecoveryAsync(entities.DiscussionId, "k1", Boot.AddMinutes(1)));
        Assert.Equal(RestartRecoveryClaim.RateLimited, await store.TryClaimRestartRecoveryAsync(entities.DiscussionId, "k2", Boot.AddMinutes(10)));

        await entities.PatchAsync(entities.DiscussionId, new JsonObject { ["status"] = DiscussionStatus.Archived });
        Assert.Equal(RestartRecoveryClaim.Refused, await store.TryClaimRestartRecoveryAsync(entities.DiscussionId, "k3", Boot.AddHours(2)));
    }

    // ---- Status mapping the waker relies on (FromSessionStatus is unchanged) ----

    [Theory]
    [InlineData("Stopped", "maintenance_restart", DiscussionStatus.Idle)]
    [InlineData("Stopped", "orphaned_on_restart", DiscussionStatus.Idle)]
    [InlineData("Stopped", "user_stopped", DiscussionStatus.Stopped)]
    [InlineData("Error", null, DiscussionStatus.Stopped)]
    [InlineData("Idle", "completed", DiscussionStatus.Idle)]
    [InlineData("Active", "completed", DiscussionStatus.Thinking)]
    public void Session_status_mapping_is_unchanged(string status, string? stopReason, string expected)
    {
        Assert.Equal(expected, DiscussionStatus.FromSessionStatus(status, "chat", stopReason));
    }

    // ---- The waker, end to end against a fake port ----

    [Fact]
    public async Task The_6_Nov_case_wakes_once_with_a_visible_reason()
    {
        var port = new FakePort();
        port.Add(Discussion(), Stopped("maintenance_restart"), LastRecord);

        var recovery = Recovery(port);
        await recovery.RunAsync(CancellationToken.None);

        var wake = Assert.Single(port.Wakes);
        Assert.Equal("d0534d08", wake.DiscussionId);
        Assert.Equal(RestartRecoveryPolicy.Key("d0534d08", "3f02ef9566b2", LastRecord), wake.Key);
        Assert.StartsWith("A restart interrupted your last turn at 13:19 Zurich time (12:19:46Z)", wake.Content);
        Assert.Contains("don't run it again", wake.Content);
        Assert.Equal("restart_interrupted_turn", wake.Metadata.GetProperty("reason").GetString());
        // The session is stopped until the woken turn runs: no leftover thinking.
        Assert.Equal(DiscussionStatus.Idle, port.Status("d0534d08"));
    }

    [Fact]
    public async Task A_second_boot_does_not_wake_the_same_interruption_again()
    {
        var port = new FakePort();
        port.Add(Discussion(), Stopped("maintenance_restart"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        // Boot again before anything ran: still thinking at boot, same interruption.
        port.SetStatus("d0534d08", DiscussionStatus.Thinking);
        await Recovery(port, Boot.AddMinutes(3)).RunAsync(CancellationToken.None);

        Assert.Single(port.Wakes);
    }

    [Fact]
    public async Task A_resumed_turn_that_orphans_itself_again_within_30_minutes_is_not_woken()
    {
        var port = new FakePort();
        port.Add(Discussion(), Stopped("maintenance_restart"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        // The woken turn rebuilt again: new interruption, 10 minutes later.
        port.SetStatus("d0534d08", DiscussionStatus.Thinking);
        port.SetLastRecord("d0534d08", LastRecord.AddMinutes(10));
        await Recovery(port, Boot.AddMinutes(10)).RunAsync(CancellationToken.None);

        Assert.Single(port.Wakes);
        Assert.Equal(DiscussionStatus.Idle, port.Status("d0534d08"));
    }

    [Fact]
    public async Task A_user_stop_never_wakes_and_shows_stopped()
    {
        var port = new FakePort();
        port.Add(Discussion(), Stopped("user_stopped"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Equal(DiscussionStatus.Stopped, port.Status("d0534d08"));
    }

    [Fact]
    public async Task Idle_at_boot_is_not_probed()
    {
        var port = new FakePort();
        port.Add(Discussion(status: DiscussionStatus.Idle), Stopped("maintenance_restart"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Empty(port.Probes);
        Assert.Equal(0, port.ComputeChecks);
    }

    [Fact]
    public async Task A_discussion_archived_while_waiting_for_compute_never_wakes()
    {
        var port = new FakePort { UnreachableChecks = 2 };
        port.Add(Discussion(), Stopped("maintenance_restart"), LastRecord);
        port.OnComputeCheck = () => port.SetStatus("d0534d08", DiscussionStatus.Archived);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Equal(DiscussionStatus.Archived, port.Status("d0534d08"));
    }

    [Fact]
    public async Task Live_thinking_at_boot_is_left_to_heartbeat()
    {
        var port = new FakePort();
        port.Add(Discussion(type: "live"), Stopped("maintenance_restart"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Empty(port.Probes);
    }

    [Fact]
    public async Task A_session_read_that_still_says_active_is_asked_again_until_it_converges()
    {
        // RedCompute republishes its boot recovery to the read model a moment after it answers.
        var port = new FakePort();
        port.Add(Discussion(), new Probe(true, "Active", "completed", "p"), LastRecord);
        port.OnDelay = delay =>
        {
            if (delay == Timing.Recheck) port.SetProbe("d0534d08", Stopped("maintenance_restart"));
        };
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Single(port.Wakes);
    }

    [Fact]
    public async Task A_session_that_really_is_running_is_left_alone()
    {
        var port = new FakePort();
        port.Add(Discussion(), new Probe(true, "Active", null, "p"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Equal(DiscussionStatus.Thinking, port.Status("d0534d08"));
        Assert.Equal(Timing.Passes, port.Probes.Count);
    }

    [Fact]
    public async Task Overnight_off_clears_thinking_without_waking()
    {
        var port = new FakePort();
        port.Add(Discussion(lastActivity: Boot.AddHours(-10)), Stopped("orphaned_on_restart"), Boot.AddHours(-10));
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Equal(DiscussionStatus.Idle, port.Status("d0534d08"));
    }

    [Fact]
    public async Task Several_orphans_wake_one_at_a_time_newest_first()
    {
        var port = new FakePort();
        port.Add(Discussion("aaaa0001", "s1", lastActivity: Boot.AddMinutes(-20)), Stopped("maintenance_restart"), Boot.AddMinutes(-20));
        port.Add(Discussion("aaaa0002", "s2", lastActivity: Boot.AddMinutes(-1)), Stopped("maintenance_restart"), Boot.AddMinutes(-1));
        port.Add(Discussion("aaaa0003", "s3", lastActivity: Boot.AddMinutes(-5)), Stopped("orphaned_on_restart"), Boot.AddMinutes(-5));
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Equal(["aaaa0002", "aaaa0003", "aaaa0001"], port.Wakes.Select(w => w.DiscussionId).ToArray());
        // Two stagger pauses between three wakes, none before the first.
        Assert.Equal(2, port.Delays.Count(d => d == Timing.Stagger));
        Assert.Equal(Timing.Stagger, port.Delays[port.Delays.IndexOf(Timing.Settle) + 1]);
    }

    [Fact]
    public async Task Compute_that_never_answers_changes_nothing()
    {
        var port = new FakePort { UnreachableChecks = int.MaxValue };
        port.Add(Discussion(), Stopped("maintenance_restart"), LastRecord);
        await Recovery(port).RunAsync(CancellationToken.None);

        Assert.Empty(port.Wakes);
        Assert.Empty(port.Probes);
        Assert.Equal(DiscussionStatus.Thinking, port.Status("d0534d08"));
    }

    // ---- helpers ----

    private static readonly RestartRecoveryTiming Timing = new(
        ComputePoll: TimeSpan.FromSeconds(5), ComputeMaxWait: TimeSpan.FromSeconds(15),
        Settle: TimeSpan.FromSeconds(20), Passes: 3, Recheck: TimeSpan.FromSeconds(60), Stagger: TimeSpan.FromSeconds(21));

    private static RestartRecovery Recovery(FakePort port, DateTimeOffset? now = null)
        => new(port, NullLogger.Instance, () => now ?? Boot, Timing);

    private static RestartRecoveryVerdict Qualify(
        string statusAtBoot = DiscussionStatus.Thinking, DiscussionRead? current = null, Probe? probe = null,
        DateTimeOffset? lastSeen = null, bool noLastSeen = false)
        => RestartRecoveryPolicy.Qualify(statusAtBoot, "3f02ef9566b2", current ?? Discussion(),
            probe ?? Stopped("maintenance_restart"), noLastSeen ? null : lastSeen ?? LastRecord, Boot);

    private static Probe Stopped(string? reason) => new(true, "Stopped", reason, "57af1016");

    private static DiscussionRead Discussion(string id = "d0534d08", string sessionId = "3f02ef9566b2",
        string status = DiscussionStatus.Thinking, string type = "chat", DateTimeOffset? lastActivity = null)
        => new(id, "Reactions in Nova", sessionId, status, Boot.UtcDateTime.AddHours(-14),
            (lastActivity ?? new DateTimeOffset(2026, 11, 6, 12, 16, 20, TimeSpan.Zero)).UtcDateTime, 15, null,
            "owner-1", Guid.NewGuid(), "nova-agent", type);

    private static JsonObject Record(string key, DateTimeOffset at) => new() { ["key"] = key, ["at"] = at.ToString("O") };

    private sealed record Wake(string DiscussionId, string Content, JsonElement Metadata, string Key);

    private sealed class FakePort : IRestartRecoveryPort
    {
        private readonly Dictionary<string, (DiscussionRead Discussion, Probe Probe, DateTimeOffset? LastRecord, JsonNode? Claim)> rows = [];
        public List<Wake> Wakes { get; } = [];
        public List<string> Probes { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public int ComputeChecks { get; private set; }
        public int UnreachableChecks { get; set; }
        public Action? OnComputeCheck { get; set; }
        public Action<TimeSpan>? OnDelay { get; set; }

        public void Add(DiscussionRead discussion, Probe probe, DateTimeOffset? lastRecord)
            => rows[discussion.Id] = (discussion, probe, lastRecord, null);

        public string Status(string id) => rows[id].Discussion.Status;
        public void SetStatus(string id, string status) => rows[id] = rows[id] with { Discussion = rows[id].Discussion with { Status = status } };
        public void SetProbe(string id, Probe probe) => rows[id] = rows[id] with { Probe = probe };
        public void SetLastRecord(string id, DateTimeOffset at) => rows[id] = rows[id] with { LastRecord = at };

        private string IdOf(Guid entityId) => rows.Single(r => r.Value.Discussion.EntityId == entityId).Key;
        private string IdOfSession(string sessionId) => rows.Single(r => r.Value.Discussion.SessionId == sessionId).Key;

        public Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiscussionRead>>(rows.Values.Select(r => r.Discussion)
                .Where(d => !DiscussionStatus.IsClosed(d.Status)).ToList());

        public Task<DiscussionRead?> GetAsync(Guid entityId, CancellationToken ct)
            => Task.FromResult<DiscussionRead?>(rows[IdOf(entityId)].Discussion);

        public Task<bool> ComputeReachableAsync(CancellationToken ct)
        {
            ComputeChecks++;
            OnComputeCheck?.Invoke();
            if (UnreachableChecks <= 0) return Task.FromResult(true);
            UnreachableChecks--;
            return Task.FromResult(false);
        }

        public Task<Probe> ProbeAsync(string sessionId, CancellationToken ct)
        {
            Probes.Add(sessionId);
            return Task.FromResult(rows[IdOfSession(sessionId)].Probe);
        }

        public Task<DateTimeOffset?> LastRecordAtAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(rows[IdOfSession(sessionId)].LastRecord);

        // Same rules as DiscussionStore.TryClaimRestartRecoveryAsync, persisted on the row.
        public Task<RestartRecoveryClaim> TryClaimAsync(Guid entityId, string key, DateTimeOffset now, CancellationToken ct)
        {
            var id = IdOf(entityId);
            if (DiscussionStatus.IsClosed(rows[id].Discussion.Status)) return Task.FromResult(RestartRecoveryClaim.Refused);
            var decision = RestartRecoveryPolicy.Decide(rows[id].Claim, key, now);
            if (decision == RestartRecoveryClaim.Claimed)
                rows[id] = rows[id] with { Claim = Record(key, now) };
            return Task.FromResult(decision);
        }

        public Task SetStatusAsync(Guid entityId, string status, CancellationToken ct)
        {
            var id = IdOf(entityId);
            if (!DiscussionStatus.IsClosed(rows[id].Discussion.Status)) SetStatus(id, status);
            return Task.CompletedTask;
        }

        public Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct)
        {
            if (DiscussionStatus.IsClosed(rows[discussion.Id].Discussion.Status)) return Task.FromResult(false);
            Wakes.Add(new(discussion.Id, content, metadata, key));
            return Task.FromResult(true);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            OnDelay?.Invoke(delay);
            return Task.CompletedTask;
        }
    }
}
