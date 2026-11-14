using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>What to do about a discussion whose last turn failed.</summary>
public enum TransientTurnVerdict
{
    /// <summary>Nothing to do: the last turn did not fail transiently, or it was already handled.</summary>
    None,
    /// <summary>A retry is owed but its backoff has not run out yet.</summary>
    NotYet,
    /// <summary>Wake the discussion now, with this attempt number.</summary>
    Wake,
    /// <summary>Every retry failed the same way: tell the owner once and stop.</summary>
    Exhausted,
}

/// <summary>
/// The decision table for retrying a Nova discussion's own turn after a transient network or provider
/// failure. Pure, so it is tested exactly. RedCompute classifies the error (lastTurn.transient); Nova
/// only decides when to wake.
/// </summary>
/// <remarks>
/// 2026-11-14: a DNS outage of about an hour. LIVE's turn and two orchestrators' turns failed with
/// "Can't reach the API server ... (ENOTFOUND)" once the CLI's own ~4.5-minute retry gave up, and sat
/// idle for three hours until Laurent typed "You got interrupted, continue". RestartRecovery covers
/// only restarts; delegated workers are retried by RedCompute (TransientTurnRetries); this is the
/// discussion's own turn.
/// </remarks>
public static class TransientTurnRecoveryPolicy
{
    public const string EventSource = "transient-retry";

    /// <summary>Discussion data key: <c>{ turnId, attempt, at, state }</c> of the last failure handled.</summary>
    public const string DataKey = "transient_retry";

    /// <summary>Wait before each wake, from the end of the failed turn (RedCompute uses the same schedule).</summary>
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30),
    ];

    /// <summary>A failure older than this is left alone (Nova or the PC was down).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    /// <summary>A failure ending this soon after the previous wake is that wake's turn failing again,
    /// so it continues the count. Later, it is a new failure and the count starts over.</summary>
    public static readonly TimeSpan ChainGap = TimeSpan.FromMinutes(40);

    public sealed record Decision(TransientTurnVerdict Verdict, int Attempt = 0, DateTimeOffset? DueAt = null);

    public static Decision Qualify(DiscussionRead discussion, RedComputeClient.SessionLastTurn probe,
        JsonNode? previous, DateTimeOffset now)
    {
        if (DiscussionStatus.IsClosed(discussion.Status) || DiscussionApplicationOwner.IsForeign(discussion)
            || discussion.Type == HeartbeatService.DiscussionType || discussion.SessionId is null)
            return new(TransientTurnVerdict.None);
        if (!probe.Reachable || probe.TurnId is null || probe.EndedAt is not { } endedAt) return new(TransientTurnVerdict.None);
        if (probe.Outcome != "failed" || !probe.Transient) return new(TransientTurnVerdict.None);
        var idle = probe.Status == "Idle"
            || probe.Status == "Stopped" && probe.StopReason is "idle_timeout" or "idle_ttl_expired";
        if (!idle) return new(TransientTurnVerdict.None);
        if (now - endedAt > Window) return new(TransientTurnVerdict.None);

        var record = previous as JsonObject;
        if (Text(record, "turnId") == probe.TurnId) return new(TransientTurnVerdict.None);
        var attempt = 1;
        if (Text(record, "state") == "woken" && Time(record, "at") is { } wokenAt
            && endedAt >= wokenAt && endedAt - wokenAt <= ChainGap
            && record!["attempt"] is JsonValue a && a.TryGetValue<int>(out var last))
            attempt = last + 1;
        if (attempt > Delays.Length) return new(TransientTurnVerdict.Exhausted, attempt - 1);
        var due = endedAt + Delays[attempt - 1];
        return now < due ? new(TransientTurnVerdict.NotYet, attempt, due) : new(TransientTurnVerdict.Wake, attempt, due);
    }

    /// <summary>One failed turn of one discussion's session.</summary>
    public static string Key(string discussionId, string sessionId, string turnId)
        => $"{EventSource}:{discussionId}:{sessionId}:{turnId}";

    /// <summary>What the woken turn reads, and what Laurent sees in the thread. The first line says why it woke.</summary>
    public static string Content(DateTimeOffset failedAt, string? error, int attempt)
        => $"Your last turn failed at {Zurich(failedAt):HH:mm} Zurich time ({failedAt.UtcDateTime:HH:mm:ss}Z) on a transient network or provider error: {Clip(error)}.\n"
            + "It was the connection or the model service, not something you did. Check where you were, redo only what didn't finish, and carry on.\n"
            + $"Automatic retry {attempt} of {Delays.Length}.";

    /// <summary>The one line LIVE gets when the retries ran out.</summary>
    public static string ExhaustedNotice(DiscussionRead discussion, string? error)
        => $"\"{Clip(discussion.Title ?? discussion.Id, 80)}\" stopped on a network or provider error and {Delays.Length} automatic retries didn't get through: {Clip(error, 160)}";

    private static string Clip(string? value, int max = 300)
    {
        var flat = (value ?? "unknown error").ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)] + "...";
    }

    private static string? Text(JsonObject? record, string name)
        => record?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static DateTimeOffset? Time(JsonObject? record, string name)
        => Text(record, name) is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t : null;

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

/// <summary>Everything the recovery touches, so it is tested without a kernel.</summary>
internal interface ITransientTurnRecoveryPort
{
    Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct);
    Task<RedComputeClient.SessionLastTurn> ProbeAsync(string sessionId, CancellationToken ct);
    /// <summary>Decides under the discussion's write gate against its stored record, and records a Wake
    /// or Exhausted decision before it is acted on. Null when the discussion is closed, foreign or gone.</summary>
    Task<TransientTurnRecoveryPolicy.Decision?> TryClaimAsync(DiscussionRead discussion, RedComputeClient.SessionLastTurn probe,
        DateTimeOffset now, CancellationToken ct);
    Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct);
    Task NotifyOwnerAsync(DiscussionRead discussion, string content, string key, CancellationToken ct);
}

/// <summary>
/// Every <see cref="Interval"/>, looks at open discussions active within the retry window and wakes
/// each one whose own last turn failed transiently, on the backoff schedule, with a visible
/// <c>&lt;nova-event source="transient-retry"&gt;</c>. When the retries run out it tells the owner in
/// LIVE once (a LIVE discussion's own failure stays in LIVE's thread).
/// </summary>
public sealed class TransientTurnRecovery
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ITransientTurnRecoveryPort port;
    private readonly ILogger logger;
    private readonly Func<DateTimeOffset> clock;

    public TransientTurnRecovery(DiscussionStore store, RedComputeClient redCompute, EventInjector injector, LiveEvents live,
        ILogger<TransientTurnRecovery> logger)
        : this(new Port(store, redCompute, injector, live), logger, null)
    {
    }

    internal TransientTurnRecovery(ITransientTurnRecoveryPort port, ILogger logger, Func<DateTimeOffset>? clock)
    {
        this.port = port;
        this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Transient turn recovery pass failed"); }
        }
    }

    /// <summary>One pass. Returns the discussions woken.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock();
        var woken = 0;
        var candidates = (await port.ListOpenAsync(ct))
            .Where(d => d.SessionId is not null && d.Status != DiscussionStatus.Thinking)
            .Where(d => d.Type != HeartbeatService.DiscussionType && !DiscussionApplicationOwner.IsForeign(d))
            .Where(d => now - Utc(d.LastActivity) <= TransientTurnRecoveryPolicy.Window + TransientTurnRecoveryPolicy.Delays[^1]);
        foreach (var discussion in candidates)
        {
            var probe = await port.ProbeAsync(discussion.SessionId!, ct);
            // Most discussions stop here; the stored record is read only for a transient failure.
            if (!probe.Reachable || probe.Outcome != "failed" || !probe.Transient || probe.TurnId is null) continue;
            var decision = await port.TryClaimAsync(discussion, probe, now, ct);
            if (decision is null) continue;
            var key = TransientTurnRecoveryPolicy.Key(discussion.Id, discussion.SessionId!, probe.TurnId!);
            if (decision.Verdict == TransientTurnVerdict.Wake)
            {
                var metadata = JsonSerializer.SerializeToElement(new
                {
                    reason = "transient_turn_failure",
                    sessionId = discussion.SessionId,
                    failedTurnId = probe.TurnId,
                    failedAt = probe.EndedAt!.Value.UtcDateTime.ToString("O"),
                    error = probe.Error,
                    attempt = decision.Attempt,
                    maxAttempts = TransientTurnRecoveryPolicy.Delays.Length,
                });
                var forwarded = await port.WakeAsync(discussion,
                    TransientTurnRecoveryPolicy.Content(probe.EndedAt!.Value, probe.Error, decision.Attempt), metadata, key, ct);
                woken++;
                if (forwarded)
                    logger.LogInformation("Transient recovery: woke discussion {DiscussionId} (attempt {Attempt}) after {Error}",
                        discussion.Id, decision.Attempt, probe.Error);
                else
                    logger.LogWarning("Transient recovery: the wake for discussion {DiscussionId} is in the thread but did not reach its session",
                        discussion.Id);
            }
            else if (decision.Verdict == TransientTurnVerdict.Exhausted)
            {
                logger.LogWarning("Transient recovery: discussion {DiscussionId} still fails after {Attempts} retries: {Error}",
                    discussion.Id, decision.Attempt, probe.Error);
                if (discussion.Type != "live")
                    await port.NotifyOwnerAsync(discussion, TransientTurnRecoveryPolicy.ExhaustedNotice(discussion, probe.Error), key + ":exhausted", ct);
            }
        }
        return woken;
    }

    private static DateTimeOffset Utc(DateTime value)
        => new(DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind).ToUniversalTime());

    private sealed class Port(DiscussionStore store, RedComputeClient redCompute, EventInjector injector, LiveEvents live)
        : ITransientTurnRecoveryPort
    {
        public async Task<IReadOnlyList<DiscussionRead>> ListOpenAsync(CancellationToken ct) => await store.ListOpenAsync(ct: ct);

        public Task<RedComputeClient.SessionLastTurn> ProbeAsync(string sessionId, CancellationToken ct)
            => redCompute.ProbeLastTurnAsync(sessionId, ct);

        public Task<TransientTurnRecoveryPolicy.Decision?> TryClaimAsync(DiscussionRead discussion,
            RedComputeClient.SessionLastTurn probe, DateTimeOffset now, CancellationToken ct)
            => store.TryClaimTransientRetryAsync(discussion.EntityId, probe, now, ct);

        // Unsolicited: may reach an open discussion, never revive a closed one. The key makes the
        // stream record and RedCompute's admission idempotent too.
        public Task<bool> WakeAsync(DiscussionRead discussion, string content, JsonElement metadata, string key, CancellationToken ct)
            => injector.InjectIfOpenAsync(discussion, content, TransientTurnRecoveryPolicy.EventSource,
                metadata, discussion.OwnerId, key, ct);

        public Task NotifyOwnerAsync(DiscussionRead discussion, string content, string key, CancellationToken ct)
            => live.PostForOwnerAsync(discussion, TransientTurnRecoveryPolicy.EventSource, content, key, ct);
    }
}
