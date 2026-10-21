using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leaf.Plugins.Nova;

/// <summary>Coarse Agent activity phases carried by <see cref="AgentActivitySignals.EventType"/>.</summary>
public static class AgentActivityPhase
{
    public const string Thinking = "thinking";
    public const string Speaking = "speaking";
    public const string Working = "working";
    public const string Idle = "idle";
}

/// <summary>
/// One transcript record reduced to an activity marker. <see cref="Phase"/> is null for
/// records that say nothing about the Agent's activity (prompts, errors, images).
/// </summary>
public readonly record struct AgentActivityMarker(long Id, string? Phase);

/// <summary>
/// A content-free observation of one session: status, confidentiality and its newest
/// records as markers. Text, tool names and arguments never leave the probe.
/// </summary>
public sealed record AgentActivityObservation(
    string? Status, bool Confidential, IReadOnlyList<AgentActivityMarker> Markers);

public interface IAgentActivityProbe
{
    /// <summary>Null when the session could not be read; the caller simply tries again.</summary>
    Task<AgentActivityObservation?> ProbeAsync(string sessionId, CancellationToken ct);
}

/// <summary>Reads the bounded newest-record tail of a RedCompute session.</summary>
public sealed class RedComputeAgentActivityProbe(RedComputeClient redCompute) : IAgentActivityProbe
{
    internal const int Tail = 16;

    public async Task<AgentActivityObservation?> ProbeAsync(string sessionId, CancellationToken ct)
    {
        using var document = await redCompute.GetSessionRawAsync(sessionId, ct, Tail);
        return document is null ? null : Read(document.RootElement);
    }

    internal static AgentActivityObservation? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("session", out var session)
            || session.ValueKind != JsonValueKind.Object)
            return null;
        var status = session.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        var confidential = session.TryGetProperty("confidential", out var c) && c.ValueKind == JsonValueKind.True;

        var markers = new List<AgentActivityMarker>();
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt64(out var recordId))
                    continue;
                markers.Add(new(recordId, PhaseOf(message)));
            }
        }
        return new(status, confidential, markers);
    }

    /// <summary>
    /// Visible assistant text is speaking; tool calls and their results are working.
    /// Reasoning does not flip a turn back to thinking, which keeps the signal coarse.
    /// </summary>
    internal static string? PhaseOf(JsonElement message)
    {
        var role = String(message, "role");
        var eventType = String(message, "eventType") ?? "text";
        return eventType switch
        {
            "text" when role == "assistant" && !string.IsNullOrWhiteSpace(String(message, "content"))
                => AgentActivityPhase.Speaking,
            "tool_use" or "tool_call" or "tool_result" => AgentActivityPhase.Working,
            _ => null,
        };
    }

    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

/// <summary>
/// Publishes content-free <c>nova.agent.activity</c> signals so peripheral displays can
/// show what an Agent is doing. Observe-only: callers on the turn path only take an
/// in-memory lock and enqueue; one background pump publishes, because bus handlers run
/// inline in <see cref="IPluginEvents.PublishAsync"/> and every event is mirrored into
/// <c>os:events</c>. Nova's backend never sees the live stream, so while a turn runs a
/// watcher reads the session's bounded record tail to find speaking/working and the end
/// of the turn. Phases are published only on change, per discussion.
/// </summary>
/// <remarks>
/// Payload: <c>agentId</c>, <c>phase</c>, <c>userFacing</c>, <c>at</c>, <c>turn</c>.
/// <c>turn</c> is an opaque process-local id shared by every phase of one turn, so a
/// consumer can tell overlapping discussions of the same Agent apart without a
/// discussion id. Confidential discussions publish nothing. State is in-memory only:
/// after a restart nothing is replayed, so no stale <c>thinking</c> can appear.
/// </remarks>
public sealed class AgentActivitySignals : IDisposable
{
    public const string EventType = "nova.agent.activity";
    internal const int QueueCapacity = 256;
    private const int QuietPollsToEnd = 2;
    private const int MaxConsecutiveProbeFailures = 30;

    private readonly IPluginEvents events;
    private readonly IAgentActivityProbe probe;
    private readonly ILogger logger;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan startGrace;
    private readonly TimeSpan maxWatch;
    private readonly Channel<JsonObject> queue;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<string, Activity> activities = new(StringComparer.Ordinal);

    public AgentActivitySignals(
        IPluginEvents events,
        IAgentActivityProbe probe,
        ILogger<AgentActivitySignals>? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? startGrace = null,
        TimeSpan? maxWatch = null)
    {
        this.events = events;
        this.probe = probe;
        this.logger = logger ?? NullLogger<AgentActivitySignals>.Instance;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        // The admitted message needs a moment to reach the session loop.
        this.startGrace = startGrace ?? TimeSpan.FromSeconds(10);
        this.maxWatch = maxWatch ?? TimeSpan.FromHours(3);
        queue = Channel.CreateBounded<JsonObject>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => this.logger.LogWarning("Agent activity signal dropped: publish queue full"));
        _ = Task.Run(PumpAsync, CancellationToken.None);
    }

    /// <summary>
    /// A turn was admitted into the discussion's session: a person's message
    /// (<paramref name="userFacing"/> true) or an injected heartbeat, automation,
    /// callback or delegation event (false).
    /// </summary>
    public void TurnAdmitted(DiscussionRead discussion, bool userFacing)
        => Observe(() => Begin(discussion, userFacing));

    /// <summary>
    /// The single discussion status write point changed a status. A write to thinking
    /// discovers turns Nova did not admit itself (for example queued input RedCompute
    /// delivered later); other writes end a turn only when no watcher owns it.
    /// </summary>
    public void StatusWritten(DiscussionRead discussion)
        => Observe(() =>
        {
            if (discussion.Status == DiscussionStatus.Thinking)
            {
                Begin(discussion, userFacing: null);
                return;
            }
            if (!IsEligible(discussion, out _))
            {
                Forget(discussion.Id);
                return;
            }
            lock (gate)
            {
                if (activities.TryGetValue(discussion.Id, out var activity)
                    && activity.Watch is null
                    && activity.Phase != AgentActivityPhase.Idle)
                {
                    activity.Phase = AgentActivityPhase.Idle;
                    Enqueue(activity);
                }
            }
        });

    private void Begin(DiscussionRead discussion, bool? userFacing)
    {
        if (!IsEligible(discussion, out var agentId))
        {
            Forget(discussion.Id);
            return;
        }
        // Unknown origin (status reconciliation) defaults to the discussion's kind.
        var facing = (userFacing ?? true) && discussion.Type != HeartbeatService.DiscussionType;
        lock (gate)
        {
            if (!activities.TryGetValue(discussion.Id, out var activity) || activity.AgentId != agentId)
            {
                if (activity is not null) activity.Watch?.Cancel();
                activity = new Activity(agentId);
                activities[discussion.Id] = activity;
            }
            activity.Admissions++;
            if (activity.Phase == AgentActivityPhase.Idle)
            {
                activity.Turn = Guid.NewGuid().ToString("N");
                activity.UserFacing = facing;
                activity.Phase = AgentActivityPhase.Thinking;
                Enqueue(activity);
            }
            else if (userFacing == true && facing && !activity.UserFacing)
            {
                // A person joined a running automation turn (their input is queued
                // behind it): the activity is user-facing from now until idle.
                activity.UserFacing = true;
                Enqueue(activity);
            }
            if (activity.Watch is null && !string.IsNullOrWhiteSpace(discussion.SessionId))
                StartWatch(discussion.Id, discussion.SessionId, activity);
        }
    }

    private void StartWatch(string discussionId, string sessionId, Activity activity)
    {
        var watch = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        activity.Watch = watch;
        _ = Task.Run(() => WatchAsync(discussionId, sessionId, activity, watch), CancellationToken.None);
    }

    private async Task WatchAsync(
        string discussionId, string sessionId, Activity activity, CancellationTokenSource watch)
    {
        var ct = watch.Token;
        var clock = Stopwatch.StartNew();
        long? baseline = null;
        var sawRunning = false;
        var quiet = 0;
        var failures = 0;
        var first = true;
        var graceFrom = TimeSpan.Zero;
        var seen = Volatile.Read(ref activity.Admissions);
        try
        {
            while (clock.Elapsed < maxWatch)
            {
                if (!first) await Task.Delay(pollInterval, ct);
                first = false;

                AgentActivityObservation? observed;
                try
                {
                    observed = await probe.ProbeAsync(sessionId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogDebug("Agent activity probe failed ({Error})", ex.GetType().Name);
                    observed = null;
                }
                if (observed is null)
                {
                    // An unreadable session cannot show the turn is still running.
                    if (++failures >= MaxConsecutiveProbeFailures) break;
                    continue;
                }
                failures = 0;
                if (observed.Confidential)
                {
                    Forget(discussionId, activity);
                    return;
                }

                var admissions = Volatile.Read(ref activity.Admissions);
                if (admissions != seen)
                {
                    // More input was admitted (for example queued behind this turn):
                    // its turn may not have started yet, so wait for it afresh.
                    seen = admissions;
                    sawRunning = false;
                    quiet = 0;
                    graceFrom = clock.Elapsed;
                }

                if (baseline is null)
                {
                    // Records already present belong to earlier turns.
                    baseline = observed.Markers.Count == 0 ? 0 : observed.Markers.Max(marker => marker.Id);
                }
                else if (Latest(observed.Markers, baseline.Value) is { } phase)
                {
                    sawRunning = true;
                    Advance(discussionId, activity, watch, phase);
                }

                if (observed.Status is "Active" or "Starting")
                {
                    sawRunning = true;
                    quiet = 0;
                    continue;
                }
                if (!sawRunning && clock.Elapsed - graceFrom < startGrace) continue;
                if (++quiet < QuietPollsToEnd) continue;
                // Settled, unless input was admitted since the last read.
                if (TryFinish(discussionId, activity, watch, seen)) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Forgotten or replaced: whoever cancelled already owns the outcome.
        }
        catch (Exception ex)
        {
            logger.LogWarning("Agent activity watch failed ({Error})", ex.GetType().Name);
        }
        finally
        {
            TryFinish(discussionId, activity, watch, admissions: null);
            watch.Dispose();
        }
    }

    private static string? Latest(IReadOnlyList<AgentActivityMarker> markers, long baseline)
    {
        AgentActivityMarker? latest = null;
        foreach (var marker in markers)
            if (marker.Id > baseline && marker.Phase is not null
                && (latest is null || marker.Id > latest.Value.Id))
                latest = marker;
        return latest?.Phase;
    }

    private void Advance(string discussionId, Activity activity, CancellationTokenSource watch, string phase)
    {
        lock (gate)
        {
            if (!Owns(discussionId, activity, watch)
                || activity.Phase == AgentActivityPhase.Idle
                || activity.Phase == phase)
                return;
            activity.Phase = phase;
            Enqueue(activity);
        }
    }

    /// <summary>
    /// Ends the watched turn. Returns false, leaving it running, only when
    /// <paramref name="admissions"/> is given and new input arrived since.
    /// </summary>
    private bool TryFinish(string discussionId, Activity activity, CancellationTokenSource watch, int? admissions)
    {
        lock (gate)
        {
            if (!Owns(discussionId, activity, watch)) return true;
            if (admissions is not null && activity.Admissions != admissions) return false;
            activity.Watch = null;
            if (activity.Phase != AgentActivityPhase.Idle)
            {
                activity.Phase = AgentActivityPhase.Idle;
                Enqueue(activity);
            }
        }
        return true;
    }

    private bool Owns(string discussionId, Activity activity, CancellationTokenSource watch)
        => activities.TryGetValue(discussionId, out var current)
            && ReferenceEquals(current, activity)
            && ReferenceEquals(activity.Watch, watch);

    /// <summary>Drops tracking silently: a confidential discussion publishes nothing, not even idle.</summary>
    private void Forget(string discussionId, Activity? expected = null)
    {
        lock (gate)
        {
            if (!activities.TryGetValue(discussionId, out var activity)) return;
            if (expected is not null && !ReferenceEquals(activity, expected)) return;
            activities.Remove(discussionId);
            activity.Watch?.Cancel();
        }
    }

    private static bool IsEligible(DiscussionRead discussion, out string agentId)
    {
        agentId = "";
        if (discussion.Confidential || DiscussionApplicationOwner.IsForeign(discussion)) return false;
        if (!Guid.TryParse(discussion.AgentId, out var agent) || agent == Guid.Empty) return false;
        agentId = agent.ToString("D");
        return true;
    }

    /// <summary>Called under <see cref="gate"/>, so queue order matches state order.</summary>
    private void Enqueue(Activity activity)
        => queue.Writer.TryWrite(new JsonObject
        {
            ["agentId"] = activity.AgentId,
            ["phase"] = activity.Phase,
            ["userFacing"] = activity.UserFacing,
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["turn"] = activity.Turn,
        });

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(stopping.Token))
            {
                try
                {
                    await events.PublishAsync(EventType, payload, stopping.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Agent activity signal could not be published ({Error})", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private void Observe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Agent activity observation failed ({Error})", ex.GetType().Name);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        queue.Writer.TryComplete();
    }

    private sealed class Activity(string agentId)
    {
        public string AgentId { get; } = agentId;
        public string Turn { get; set; } = "";
        public string Phase { get; set; } = AgentActivityPhase.Idle;
        public bool UserFacing { get; set; }
        public CancellationTokenSource? Watch { get; set; }
        /// <summary>Bumped under the gate on every admission; read by the watcher.</summary>
        public int Admissions;
    }
}
