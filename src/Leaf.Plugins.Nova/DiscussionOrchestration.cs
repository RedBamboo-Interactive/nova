using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Where an Orchestrated discussion stands between turns. <see cref="DueAt"/> is when its Heartbeat is told it
/// is idle (null: nothing armed); <see cref="TurnKey"/> names the settled turn, or a watch
/// (<see cref="DiscussionOrchestration.WatchPrefix"/>) armed by time rather than by a turn; <see cref="Chain"/>
/// counts the hand-offs since its person last wrote there, which paces the next one; <see cref="Revisits"/> counts
/// the hand-offs in a row that started no turn, which backs the watch off.
/// </summary>
public sealed record OrchestrationState(DateTimeOffset? DueAt, string? TurnKey, int Chain, bool LastTurnFailed = false, int Revisits = 0)
{
    public const string DataKey = "orchestration";
    public static readonly OrchestrationState Empty = new(null, null, 0);

    public static OrchestrationState Read(JsonObject data)
    {
        if (data[DataKey] is not JsonObject o) return Empty;
        DateTimeOffset? due = o["dueAt"] is JsonValue d && d.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        var turn = o["turn"] is JsonValue t && t.TryGetValue<string>(out var turnText) ? turnText : null;
        var chain = o["chain"] is JsonValue c && c.TryGetValue<int>(out var n) && n > 0 ? n : 0;
        var failed = o["lastTurnFailed"] is JsonValue f && f.TryGetValue<bool>(out var b) && b;
        var revisits = o["revisits"] is JsonValue r && r.TryGetValue<int>(out var m) && m > 0 ? m : 0;
        return new(due, turn, chain, failed, revisits);
    }

    public JsonObject ToJson() => new()
    {
        ["dueAt"] = DueAt?.ToString("O"),
        ["turn"] = TurnKey,
        ["chain"] = Chain,
        ["lastTurnFailed"] = LastTurnFailed,
        ["revisits"] = Revisits,
    };
}

/// <summary>
/// Orchestrated discussions. The person, or an Agent acting for them, marks a discussion Orchestrated. Every
/// prompt it receives then registers a durable RedCompute completion callback. A turn that settles with nothing
/// left arms a quiet window (<see cref="Window"/>); new input cancels it. When the window ends and the session is
/// still idle, the <see cref="OrchestrationSweeper"/> tells the Agent's Heartbeat, whose
/// <c>heartbeat-orchestration</c> Skill decides: send it the next step, tell LIVE, or leave it.
/// <para>
/// Leaving it does not end the loop. A hand-off that starts no turn arms a watch, and the Heartbeat is told again
/// that it is still idle 6 minutes later, then 12, 24, 48 and every hour, so a workstream waiting on something is
/// looked at again on its own. Marking a discussion while a turn is running watches that turn too: its prompt was
/// sent before the mark, so it has no idle callback.
/// </para>
/// <para>
/// Pacing, not a cap: the window is 3 minutes for the first <see cref="FreeSteps"/> hand-offs since the person last
/// wrote in the discussion, then doubles per hand-off up to an hour. The person's message resets it.
/// </para>
/// <para>
/// The callback reuses <c>/callbacks/session-complete</c> (on the kernel's auth bypass list) with
/// <c>orchestrated=1</c> and a callbackId of its own, so it never consumes or replaces a delegation's registration
/// on the same prompt. RedCompute's outbox and the armed state on the entity are durable: a restart loses nothing.
/// </para>
/// </summary>
public static class DiscussionOrchestration
{
    internal const string QueryFlag = "orchestrated";
    internal const string CallbackPrefix = "orch-";
    internal const string EventSource = "orchestration";
    public const string SkillSlug = "heartbeat-orchestration";

    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);
    public const int FreeSteps = 5;
    internal const string WatchPrefix = "watch:";

    internal static bool IsWatch(string? turnKey) => turnKey?.StartsWith(WatchPrefix, StringComparison.Ordinal) == true;

    /// <summary>How long a watch waits after <paramref name="revisits"/> hand-offs in a row that started no turn.</summary>
    public static TimeSpan RevisitWindow(int revisits)
    {
        if (revisits <= 0) return QuietWindow;
        var window = TimeSpan.FromTicks(QuietWindow.Ticks << Math.Min(revisits, 10));
        return window < MaxWindow ? window : MaxWindow;
    }

    /// <summary>The watch armed after hand-off <paramref name="step"/>: paced like a turn, backed off by the revisits.</summary>
    internal static OrchestrationState Watching(DateTimeOffset now, int step, int revisits, bool lastTurnFailed = false)
    {
        var window = Window(step) > RevisitWindow(revisits) ? Window(step) : RevisitWindow(revisits);
        return new(now + window, $"{WatchPrefix}{step}:{revisits}", step, lastTurnFailed, revisits);
    }

    /// <summary>How long a settled turn waits before the Heartbeat is told, given the hand-offs since the person last wrote.</summary>
    public static TimeSpan Window(int chain)
    {
        if (chain < FreeSteps) return QuietWindow;
        var doublings = Math.Min(chain - FreeSteps + 1, 10);
        var window = TimeSpan.FromTicks(QuietWindow.Ticks << doublings);
        return window < MaxWindow ? window : MaxWindow;
    }

    /// <summary>Why this discussion cannot be Orchestrated, or null when it can.</summary>
    public static string? Refusal(DiscussionRead discussion)
    {
        if (discussion.Type is "live" or HeartbeatService.DiscussionType)
            return "LIVE and Heartbeat discussions are not orchestrated: the Heartbeat is the orchestrator.";
        if (discussion.Confidential)
            return "A confidential discussion cannot be orchestrated: the Heartbeat would read it.";
        if (DiscussionApplicationOwner.IsForeign(discussion))
            return "This discussion belongs to another app.";
        if (string.IsNullOrWhiteSpace(discussion.AgentId))
            return "This discussion has no Agent, so it has no Heartbeat.";
        return null;
    }

    public const string NoHeartbeat = "Its Agent has no Heartbeat, so nothing would pick it up.";

    /// <summary><see cref="Refusal"/>, plus the one condition that needs a lookup: the Agent must have an open Heartbeat.</summary>
    public static async Task<string?> RefusalAsync(DiscussionStore store, DiscussionRead discussion, CancellationToken ct)
        => Refusal(discussion) ?? (await HasHeartbeatAsync(store, discussion.AgentId, ct) ? null : NoHeartbeat);

    public static async Task<bool> HasHeartbeatAsync(DiscussionStore store, string? agentId, CancellationToken ct)
        => !string.IsNullOrWhiteSpace(agentId)
            && (await store.ListByTypeAsync(HeartbeatService.DiscussionType, agentId, ct))
                .Any(d => d.Type == HeartbeatService.DiscussionType && !DiscussionStatus.IsClosed(d.Status));

    public static bool IsActive(DiscussionRead discussion)
        => discussion.Orchestrated && Refusal(discussion) is null && !DiscussionStatus.IsClosed(discussion.Status);

    internal static string CallbackUrl(string discussionId)
        => $"http://127.0.0.1:18804/api/apps/nova/callbacks/session-complete?discussionId={Uri.EscapeDataString(discussionId)}&{QueryFlag}=1";

    /// <summary>Read-modify-write of the orchestration state under the discussion's entity gate.</summary>
    internal static Task<OrchestrationState?> UpdateAsync(IEntityStore entities, Guid entityId,
        Func<OrchestrationState, OrchestrationState> change, CancellationToken ct)
        => DiscussionEntityGate.RunAsync<OrchestrationState?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null) return null;
            var before = OrchestrationState.Read(entity.Data);
            var after = change(before);
            if (after != before)
                await entities.PatchAsync(entityId, new JsonObject { [OrchestrationState.DataKey] = after.ToJson() }, ct: ct);
            return after;
        }, ct);

    /// <summary>
    /// A prompt is about to reach an Orchestrated discussion: register its idle callback (before sending, so a fast
    /// turn cannot outrun it) and cancel any armed hand-off, since the discussion has work again. A person's prompt
    /// also resets the pacing. Best effort: a failure loses one hand-off, never the prompt.
    /// </summary>
    public static async Task RegisterPromptAsync(RedComputeClient redCompute, IEntityStore entities, DiscussionRead discussion,
        string? sessionId, string? promptMessageUid, bool fromPerson, ILogger? logger, CancellationToken ct)
    {
        if (!IsActive(discussion) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(promptMessageUid))
            return;
        try
        {
            await UpdateAsync(entities, discussion.EntityId,
                s => s with { DueAt = null, TurnKey = null, Chain = fromPerson ? 0 : s.Chain, Revisits = 0 }, ct);
            if (!await redCompute.RegisterCallbackAsync(sessionId, CallbackUrl(discussion.Id), force: true, ct,
                    callbackId: CallbackPrefix + promptMessageUid, promptMessageUid: promptMessageUid))
                logger?.LogWarning("Orchestrated discussion {Discussion}: RedCompute refused the idle callback", discussion.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Orchestrated discussion {Discussion}: idle callback not registered", discussion.Id);
        }
    }

    /// <summary>The line the discussion's own turns carry, so its Agent leaves a state worth picking up.</summary>
    public static IEnumerable<string> ContextLines(DiscussionRead discussion)
    {
        if (!IsActive(discussion)) yield break;
        yield return "This discussion is Orchestrated: the work keeps going without its person. A few minutes after it goes idle, your Heartbeat picks it up and drives it on, so leave each turn easy to pick up: where it really stands and what comes next.";
    }

    /// <summary>What the completion callback says about the turn, reduced to the orchestration decision.</summary>
    internal sealed record IdleReport(string? Status, string? TurnOutcome, string? StopReason, int PendingInputs,
        int BackgroundTasksRunning, string? Outcome, string? TurnKey);

    internal static IdleReport ReadReport(JsonElement body)
    {
        static string? Text(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Count(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : 0;
        var turnKey = Text(body, "turnId") ?? Text(body, "deliveredMessageUid") ?? Text(body, "callbackId");
        if (turnKey is { Length: > 200 }) turnKey = null;
        return new(Text(body, "status"), Text(body, "turnOutcome"), Text(body, "stopReason"),
            Count(body, "pendingInputs"), Count(body, "backgroundTasksRunning"), Text(body, "outcome"), turnKey);
    }

    /// <summary>
    /// Why this report arms nothing, or null when it does. Only a turn that settled with nothing queued counts as
    /// idle. A stop or interrupt is someone's decision, not idleness.
    /// </summary>
    internal static string? SkipReason(IdleReport report)
    {
        if (report.Outcome is "paused" or "cancelled" or "superseded") return $"outcome {report.Outcome}";
        if (report.PendingInputs > 0) return "more input queued";
        if (report.BackgroundTasksRunning > 0) return "background work still running";
        if (report.TurnOutcome is "interrupted" or "stopped") return $"turn {report.TurnOutcome}";
        if (report.Status is not "Idle") return $"session {report.Status ?? "unknown"}";
        if (report.TurnKey is null) return "no turn identity";
        return null;
    }

    /// <summary>The <c>session-complete?orchestrated=1</c> branch: arms the quiet window. Always 200, so RedCompute never retries it.</summary>
    internal static async Task<IResult> HandleCallbackAsync(JsonElement body, string discussionId,
        DiscussionStore store, IEntityStore entities, TimeProvider clock, CancellationToken ct)
    {
        var discussion = await store.GetAsync(discussionId, ct);
        if (discussion is null || !IsActive(discussion))
            return Results.Ok(new { handled = false, reason = "not orchestrated" });
        var report = ReadReport(body);
        if (SkipReason(report) is { } skip)
            return Results.Ok(new { handled = false, reason = skip });

        var now = clock.GetUtcNow();
        var armed = await UpdateAsync(entities, discussion.EntityId,
            s => s.TurnKey == report.TurnKey && s.DueAt is not null
                ? s // the same turn reported again keeps its original deadline
                : s with { DueAt = now + Window(s.Chain), TurnKey = report.TurnKey, LastTurnFailed = report.TurnOutcome == "failed", Revisits = 0 },
            ct);
        return Results.Ok(new { handled = armed is not null, dueAt = armed?.DueAt });
    }

    internal static string HeartbeatNote(DiscussionRead discussion, string why, int step, bool lastTurnFailed)
    {
        var title = NovaEventText.Field(discussion.Title, CallbackEndpoints.MaxTitle) ?? "Untitled";
        var failed = lastTurnFailed ? " Its last turn failed." : "";
        return $"""
            <nova-event source="{EventSource}" type="orchestrated-idle" discussion="{NovaEventText.Attribute(discussion.Id)}" step="{step}">
            Orchestrated discussion [{discussion.Id}] "{title}" {why}.{failed} Hand-off {step} since its person last wrote there.
            Read it: GET /api/apps/nova/discussions/{discussion.Id}/export. Decide with your {SkillSlug} Skill; without that Skill, leave it and tell LIVE-you.
            </nova-event>
            """;
    }

    internal enum HandOff { Delivered, NoHeartbeat, Failed }

    /// <summary>
    /// Tells the Agent's open Heartbeat, after its current turn. A Heartbeat without a session keeps the note queued
    /// for its next one, which counts as delivered.
    /// </summary>
    internal static async Task<HandOff> HandOffAsync(DiscussionStore store, EventInjector injector, DiscussionRead discussion,
        string why, int step, bool lastTurnFailed, string idempotencyKey, CancellationToken ct)
    {
        var heartbeat = (await store.ListByTypeAsync(HeartbeatService.DiscussionType, discussion.AgentId, ct))
            .FirstOrDefault(d => d.Type == HeartbeatService.DiscussionType && !DiscussionStatus.IsClosed(d.Status));
        if (heartbeat is null) return HandOff.NoHeartbeat;
        var metadata = JsonSerializer.SerializeToElement(new { orchestratedDiscussionId = discussion.Id, reason = why, step });
        var forwarded = await injector.InjectAsync(heartbeat, HeartbeatNote(discussion, why, step, lastTurnFailed), null, EventSource,
            metadata: metadata, idempotencyKey: idempotencyKey, redeliverOnReuse: true,
            deliveryIdempotencyKey: idempotencyKey, ct: ct, framed: true, delivery: "after-current");
        return forwarded || heartbeat.SessionId is null ? HandOff.Delivered : HandOff.Failed;
    }

    /// <summary>
    /// Turning the mark on while the discussion is idle is the person saying "take it from here": the Heartbeat is
    /// told at once, the pacing starts fresh, and a watch looks again if that starts nothing.
    /// </summary>
    internal static async Task HandOffNowAsync(DiscussionStore store, IEntityStore entities, EventInjector injector,
        DiscussionRead discussion, CancellationToken ct, TimeProvider? clock = null)
    {
        var result = await HandOffAsync(store, injector, discussion, "was just marked Orchestrated", 1, false,
            $"orchestrated-marked:{discussion.Id}:{discussion.ConversationRevision}", ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        await UpdateAsync(entities, discussion.EntityId,
            _ => result == HandOff.Delivered ? Watching(now, 1, 1) : new(null, null, 0), ct);
    }

    /// <summary>
    /// Turning the mark on while a turn is running: that turn's prompt went out before the mark, so it has no idle
    /// callback. A watch checks back in <see cref="QuietWindow"/> and hands it over once the session is idle.
    /// </summary>
    internal static Task WatchRunningTurnAsync(IEntityStore entities, DiscussionRead discussion, CancellationToken ct,
        TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return UpdateAsync(entities, discussion.EntityId,
            s => s.DueAt is null ? new(now + QuietWindow, $"{WatchPrefix}0:0", 0) : s, ct);
    }
}

/// <summary>
/// Fires armed hand-offs whose quiet window has ended. Before telling the Heartbeat it asks RedCompute whether the
/// session is still idle: a session that is working again only loses its armed state, and its next settled turn
/// arms a new one, while a watch keeps watching. Every delivered hand-off arms a watch, which a prompt reaching the
/// discussion cancels. RedCompute unreachable, or a delivery that failed, leaves the state for the next sweep.
/// </summary>
public sealed class OrchestrationSweeper(
    DiscussionStore store, IEntityStore entities, EventInjector injector, RedComputeClient redCompute,
    ILogger<OrchestrationSweeper>? logger = null, TimeProvider? clock = null)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ILogger logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
            try { await SweepAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Orchestration sweep failed; the next one retries");
            }
        }
    }

    internal async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var handed = 0;
        foreach (var discussion in await store.ListOpenAsync(ct: ct))
        {
            if (!DiscussionOrchestration.IsActive(discussion)) continue;
            var state = discussion.Orchestration ?? OrchestrationState.Empty;
            if (state.DueAt is not { } due || due > now || discussion.SessionId is null) continue;

            var probe = await redCompute.ProbeSessionAsync(discussion.SessionId, ct);
            if (!probe.Reachable) continue;
            var watch = DiscussionOrchestration.IsWatch(state.TurnKey);
            if (probe.Status != "Idle")
            {
                // A turn-armed hand-off whose session works again has a new prompt and callback: drop it.
                // A watch has no callback to wait for, so it keeps watching.
                await DiscussionOrchestration.UpdateAsync(entities, discussion.EntityId,
                    s => s.TurnKey != state.TurnKey ? s
                        : watch ? s with { DueAt = now + DiscussionOrchestration.QuietWindow }
                        : s with { DueAt = null, TurnKey = null }, ct);
                continue;
            }

            var step = state.Chain + 1;
            var why = watch && state.Revisits > 0
                ? $"is still idle: nothing has happened there since hand-off {state.Chain}"
                : "went idle";
            var result = await DiscussionOrchestration.HandOffAsync(store, injector, discussion, why, step,
                state.LastTurnFailed, $"orchestrated-idle:{discussion.Id}:{state.TurnKey}", ct);
            if (result == DiscussionOrchestration.HandOff.Failed) continue;
            var revisits = (watch ? state.Revisits : 0) + 1;
            await DiscussionOrchestration.UpdateAsync(entities, discussion.EntityId,
                s => s.TurnKey != state.TurnKey ? s
                    : result == DiscussionOrchestration.HandOff.Delivered
                        ? DiscussionOrchestration.Watching(now, step, revisits, state.LastTurnFailed)
                        : s with { DueAt = null, TurnKey = null }, ct);
            if (result == DiscussionOrchestration.HandOff.Delivered) handed++;
        }
        return handed;
    }
}
