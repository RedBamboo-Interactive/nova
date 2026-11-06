using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

public enum RestartRecoveryClaim
{
    /// <summary>Recorded on the discussion: this boot sends the wake.</summary>
    Claimed,
    /// <summary>This interruption was already woken, by an earlier boot.</summary>
    AlreadyWoken,
    /// <summary>The discussion was woken automatically less than 30 minutes ago.</summary>
    RateLimited,
    /// <summary>Closed, foreign or gone.</summary>
    Refused,
}

public enum RestartRecoveryVerdict
{
    Resume,
    /// <summary>Not thinking at boot: no turn was known to be in flight.</summary>
    NotInFlight,
    Closed,
    /// <summary>LIVE and Heartbeat recover through Heartbeat's presence path.</summary>
    Presence,
    /// <summary>Another plugin owns the discussion and its session status.</summary>
    Foreign,
    /// <summary>No session, a different session than at boot, or the session is gone.</summary>
    NoSession,
    /// <summary>RedCompute did not answer; ask again on the next pass.</summary>
    ComputeUnreachable,
    /// <summary>The session reads Active or Starting: the turn may have survived, or the
    /// session read model has not caught up with boot recovery yet. Ask again.</summary>
    StillRunning,
    /// <summary>The session is Idle: the turn ended, or something already resumed it.</summary>
    Settled,
    /// <summary>Stopped for a reason a restart does not explain (user, usage limit, idle parking), or Error.</summary>
    NotARestartStop,
    /// <summary>Restart-stopped, but the turn was last seen too long before this boot (the PC was off).</summary>
    Stale,
}

/// <summary>
/// When a discussion runs a rebuild, the restart kills its own turn and nothing wakes it: the
/// promised follow-through ("after the restart I'll verify...") silently never happens, and the
/// discussion shows <c>thinking</c> with nothing behind it. This is the decision table for waking
/// such a discussion once after boot. Pure, so the table is tested exactly.
/// </summary>
public static class RestartRecoveryPolicy
{
    public const string EventSource = "restart-recovery";

    /// <summary>Discussion data key holding the last automatic wake: <c>{ key, at }</c>.</summary>
    public const string DataKey = "restart_recovery";

    /// <summary>At most one automatic wake per discussion in this window. A resumed turn can
    /// rebuild again and orphan itself again; this stops that loop.</summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(30);

    /// <summary>How close to this boot the turn must have been seen alive. A turn that died
    /// when the PC was switched off last night is not resumed on its own this morning.</summary>
    public static readonly TimeSpan RecentActivity = TimeSpan.FromMinutes(30);

    public static RestartRecoveryVerdict Qualify(
        string statusAtBoot,
        string sessionIdAtBoot,
        DiscussionRead current,
        RedComputeClient.SessionProbe probe,
        DateTimeOffset? lastSeenAlive,
        DateTimeOffset bootedAt)
    {
        if (statusAtBoot != DiscussionStatus.Thinking) return RestartRecoveryVerdict.NotInFlight;
        if (DiscussionStatus.IsClosed(current.Status)) return RestartRecoveryVerdict.Closed;
        if (current.Type is "live" or HeartbeatService.DiscussionType) return RestartRecoveryVerdict.Presence;
        if (DiscussionApplicationOwner.IsForeign(current)) return RestartRecoveryVerdict.Foreign;
        if (current.SessionId is null || current.SessionId != sessionIdAtBoot) return RestartRecoveryVerdict.NoSession;
        if (!probe.Reachable) return RestartRecoveryVerdict.ComputeUnreachable;

        switch (probe.Status)
        {
            case null: return RestartRecoveryVerdict.NoSession;
            case "Active" or "Starting": return RestartRecoveryVerdict.StillRunning;
            case "Idle": return RestartRecoveryVerdict.Settled;
            case "Stopped" when DiscussionStatus.IsRestartRecoverableStop(probe.StopReason?.Trim()):
                break;
            default: return RestartRecoveryVerdict.NotARestartStop;
        }

        if (lastSeenAlive is not { } seen || bootedAt - seen > RecentActivity)
            return RestartRecoveryVerdict.Stale;
        return RestartRecoveryVerdict.Resume;
    }

    /// <summary>The persisted guard: same interruption never twice, and one wake per window.</summary>
    public static RestartRecoveryClaim Decide(JsonNode? previous, string key, DateTimeOffset now)
    {
        if (previous is not JsonObject record) return RestartRecoveryClaim.Claimed;
        if (record["key"] is JsonValue k && k.TryGetValue<string>(out var previousKey) && previousKey == key)
            return RestartRecoveryClaim.AlreadyWoken;
        if (record["at"] is JsonValue a && a.TryGetValue<string>(out var at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var previousAt)
            && now - previousAt < ResumeWindow)
            return RestartRecoveryClaim.RateLimited;
        return RestartRecoveryClaim.Claimed;
    }

    /// <summary>One interruption: this discussion, this session, the turn's last sign of life.
    /// Nothing is written to a stopped session's transcript, so a second boot computes the same key.</summary>
    public static string Key(string discussionId, string sessionId, DateTimeOffset lastSeenAlive)
        => $"{EventSource}:{discussionId}:{sessionId}:{lastSeenAlive.UtcDateTime:O}";

    /// <summary>What the woken turn reads, and what Laurent sees in the thread. The first line says why it woke.</summary>
    public static string Content(DateTimeOffset interruptedAt)
    {
        var local = Zurich(interruptedAt);
        return $"A restart interrupted your last turn at {local:HH:mm} Zurich time ({interruptedAt.UtcDateTime:HH:mm:ss}Z), and nothing had woken you since.\n"
            + "The restart itself already happened, so don't run it again. Check what you were in the middle of, verify it, then finish it or report where it stands.\n"
            + "This wake is automatic and happens at most once per discussion every 30 minutes.";
    }

    private static DateTimeOffset Zurich(DateTimeOffset instant)
    {
        foreach (var id in new[] { "Europe/Zurich", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return instant.ToUniversalTime();
    }
}

/// <summary>Everything the waker touches, so the orchestration is tested without a kernel.</summary>
internal interface IRestartRecoveryPort
{
    Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct);
    Task<DiscussionRead?> GetAsync(Guid entityId, CancellationToken ct);
    Task<bool> ComputeReachableAsync(CancellationToken ct);
    Task<RedComputeClient.SessionProbe> ProbeAsync(string sessionId, CancellationToken ct);
    /// <summary>When the session's newest transcript record was written, if readable.</summary>
    Task<DateTimeOffset?> LastRecordAtAsync(string sessionId, CancellationToken ct);
    Task<RestartRecoveryClaim> TryClaimAsync(Guid entityId, string key, DateTimeOffset now, CancellationToken ct);
    Task SetStatusAsync(Guid entityId, string status, CancellationToken ct);
    /// <summary>Posts the visible event and forwards it; RedCompute resumes a restart-stopped session on it.</summary>
    Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct);
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed record RestartRecoveryTiming(
    TimeSpan ComputePoll,
    TimeSpan ComputeMaxWait,
    TimeSpan Settle,
    int Passes,
    TimeSpan Recheck,
    TimeSpan Stagger)
{
    /// <summary>Settle lets RedCompute finish boot recovery and republish recovered sessions
    /// before the first read. Stagger keeps several woken threads off a machine that just booted.</summary>
    public static readonly RestartRecoveryTiming Default = new(
        ComputePoll: TimeSpan.FromSeconds(5),
        ComputeMaxWait: TimeSpan.FromMinutes(15),
        Settle: TimeSpan.FromSeconds(20),
        Passes: 3,
        Recheck: TimeSpan.FromSeconds(60),
        Stagger: TimeSpan.FromSeconds(20));
}

/// <summary>
/// Runs once per Nova start. It snapshots which open discussions were <c>thinking</c> when the
/// plugin started, which is the only durable sign a turn was in flight across the restart, then
/// waits for RedCompute and wakes each one whose session a restart stopped, once, with a visible
/// <c>&lt;nova-event source="restart-recovery"&gt;</c>. Anything else left <c>thinking</c> gets its
/// true status. LIVE and Heartbeat stay with <see cref="HeartbeatService"/>.
/// </summary>
public sealed class RestartRecovery
{
    private readonly IRestartRecoveryPort port;
    private readonly ILogger logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly RestartRecoveryTiming timing;

    public RestartRecovery(DiscussionStore store, RedComputeClient redCompute, EventInjector injector,
        ILogger<RestartRecovery> logger)
        : this(new Port(store, redCompute, injector), logger, null, null)
    {
    }

    internal RestartRecovery(IRestartRecoveryPort port, ILogger logger,
        Func<DateTimeOffset>? clock, RestartRecoveryTiming? timing)
    {
        this.port = port;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.timing = timing ?? RestartRecoveryTiming.Default;
    }

    internal sealed record BootCandidate(Guid EntityId, string DiscussionId, string SessionId, string StatusAtBoot);

    /// <summary>Wakes recorded this run, in order (tests read it).</summary>
    internal List<string> Woken { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restart recovery stopped early; discussions it had not reached keep their status");
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        // First, before any client sync can rewrite it: what was thinking when this process started.
        var bootedAt = clock();
        var candidates = (await port.ListOpenAsync(ct))
            .Where(d => d.Status == DiscussionStatus.Thinking && d.SessionId is not null)
            .Where(d => d.Type is not ("live" or HeartbeatService.DiscussionType))
            .Where(d => !DiscussionApplicationOwner.IsForeign(d))
            .OrderByDescending(d => d.LastActivity)
            .Select(d => new BootCandidate(d.EntityId, d.Id, d.SessionId!, d.Status))
            .ToList();
        if (candidates.Count == 0) return;
        logger.LogInformation("Restart recovery: {Count} discussion(s) were thinking at boot", candidates.Count);

        if (!await WaitForComputeAsync(ct))
        {
            logger.LogWarning("Restart recovery: RedCompute did not answer within {Minutes} minutes; {Count} discussion(s) left as they were",
                timing.ComputeMaxWait.TotalMinutes, candidates.Count);
            return;
        }
        await port.DelayAsync(timing.Settle, ct);

        var pending = candidates;
        for (var pass = 0; pass < timing.Passes && pending.Count > 0; pass++)
        {
            if (pass > 0) await port.DelayAsync(timing.Recheck, ct);
            var again = new List<BootCandidate>();
            foreach (var candidate in pending)
            {
                if (await HandleAsync(candidate, bootedAt, ct)) again.Add(candidate);
            }
            pending = again;
        }

        foreach (var left in pending)
            logger.LogInformation("Restart recovery: discussion {DiscussionId} still reads running; left alone", left.DiscussionId);
    }

    private async Task<bool> WaitForComputeAsync(CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            if (await port.ComputeReachableAsync(ct)) return true;
            if (waited >= timing.ComputeMaxWait) return false;
            await port.DelayAsync(timing.ComputePoll, ct);
            waited += timing.ComputePoll;
        }
    }

    /// <summary>Returns true when the candidate should be asked about again on the next pass.</summary>
    private async Task<bool> HandleAsync(BootCandidate candidate, DateTimeOffset bootedAt, CancellationToken ct)
    {
        var current = await port.GetAsync(candidate.EntityId, ct);
        if (current is null) return false;

        var probe = await port.ProbeAsync(candidate.SessionId, ct);
        DateTimeOffset? lastRecord = probe is { Reachable: true, Status: "Stopped" }
            ? await port.LastRecordAtAsync(candidate.SessionId, ct)
            : null;
        var lastSeenAlive = Latest(Utc(current.LastActivity), lastRecord);

        var verdict = RestartRecoveryPolicy.Qualify(
            candidate.StatusAtBoot, candidate.SessionId, current, probe, lastSeenAlive, bootedAt);
        switch (verdict)
        {
            case RestartRecoveryVerdict.StillRunning or RestartRecoveryVerdict.ComputeUnreachable:
                return true;
            case RestartRecoveryVerdict.Resume:
                await ResumeAsync(current, probe, lastSeenAlive!.Value, ct);
                return false;
            case RestartRecoveryVerdict.Settled or RestartRecoveryVerdict.NotARestartStop or RestartRecoveryVerdict.Stale:
                logger.LogInformation("Restart recovery: discussion {DiscussionId} not woken ({Verdict})", current.Id, verdict);
                await ClearThinkingAsync(current, probe, ct);
                return false;
            default:
                return false;
        }
    }

    private async Task ResumeAsync(DiscussionRead discussion, RedComputeClient.SessionProbe probe,
        DateTimeOffset interruptedAt, CancellationToken ct)
    {
        var key = RestartRecoveryPolicy.Key(discussion.Id, discussion.SessionId!, interruptedAt);
        var claim = await port.TryClaimAsync(discussion.EntityId, key, clock(), ct);
        if (claim != RestartRecoveryClaim.Claimed)
        {
            logger.LogInformation("Restart recovery: discussion {DiscussionId} not woken ({Claim})", discussion.Id, claim);
            await ClearThinkingAsync(discussion, probe, ct);
            return;
        }

        // The session is stopped: say so until the woken turn runs.
        await ClearThinkingAsync(discussion, probe, ct);
        if (Woken.Count > 0) await port.DelayAsync(timing.Stagger, ct);

        var metadata = JsonSerializer.SerializeToElement(new
        {
            reason = "restart_interrupted_turn",
            sessionId = discussion.SessionId,
            stopReason = probe.StopReason,
            interruptedAt = interruptedAt.UtcDateTime.ToString("O"),
        });
        var forwarded = await port.WakeAsync(discussion, RestartRecoveryPolicy.Content(interruptedAt), metadata, key, ct);
        Woken.Add(discussion.Id);
        if (forwarded)
            logger.LogInformation("Restart recovery: woke discussion {DiscussionId}, session {SessionId}, interrupted at {At:O}",
                discussion.Id, discussion.SessionId, interruptedAt);
        else
            logger.LogWarning("Restart recovery: the wake for discussion {DiscussionId} is in the thread but did not reach its session",
                discussion.Id);
    }

    private async Task ClearThinkingAsync(DiscussionRead discussion, RedComputeClient.SessionProbe probe, CancellationToken ct)
    {
        if (discussion.Status != DiscussionStatus.Thinking) return;
        var status = DiscussionStatus.FromSessionStatus(probe.Status, discussion.Type, probe.StopReason);
        if (status is not null && status != DiscussionStatus.Thinking)
            await port.SetStatusAsync(discussion.EntityId, status, ct);
    }

    private static DateTimeOffset Utc(DateTime value)
        => new(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind).ToUniversalTime());

    private static DateTimeOffset? Latest(DateTimeOffset a, DateTimeOffset? b)
        => b is { } other && other > a ? other : a;

    private sealed class Port(DiscussionStore store, RedComputeClient redCompute, EventInjector injector) : IRestartRecoveryPort
    {
        public async Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct)
            => await store.ListOpenAsync(ct: ct);

        public Task<DiscussionRead?> GetAsync(Guid entityId, CancellationToken ct) => store.GetAsync(entityId, ct);

        public async Task<bool> ComputeReachableAsync(CancellationToken ct)
            => await redCompute.GetSessionsAsync(1, ct) is not null;

        public Task<RedComputeClient.SessionProbe> ProbeAsync(string sessionId, CancellationToken ct)
            => redCompute.ProbeSessionAsync(sessionId, ct);

        public async Task<DateTimeOffset?> LastRecordAtAsync(string sessionId, CancellationToken ct)
        {
            var snapshot = await redCompute.GetSessionAsync(sessionId, ct, tail: 1);
            var last = snapshot?.Messages.LastOrDefault();
            if (last is null) return null;
            return last.RecordCreatedAt ?? new DateTimeOffset(DateTime.SpecifyKind(last.Timestamp,
                last.Timestamp.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : last.Timestamp.Kind).ToUniversalTime());
        }

        public Task<RestartRecoveryClaim> TryClaimAsync(Guid entityId, string key, DateTimeOffset now, CancellationToken ct)
            => store.TryClaimRestartRecoveryAsync(entityId, key, now, ct);

        public Task SetStatusAsync(Guid entityId, string status, CancellationToken ct)
            => store.TrySetStatusAsync(entityId, status, ct);

        // Unsolicited: may reach an open discussion, never revive a closed one (rechecked under the
        // gate). The key makes the stream record and RedCompute's admission idempotent too.
        public Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct)
            => injector.InjectIfOpenAsync(discussion, content, RestartRecoveryPolicy.EventSource,
                metadata, discussion.OwnerId, key, ct);

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
    }
}
