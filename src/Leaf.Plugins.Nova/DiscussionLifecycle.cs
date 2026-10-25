using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Two-phase archive. Phase one commits the durable intent (<c>archiving</c>) and
/// returns immediately — from that moment the discussion is closed to every status
/// writer and hidden from lists. Phase two, in the background, stops the RedCompute
/// session, verifies it is actually gone, and only then finalizes to <c>archived</c>.
/// A discussion stuck in <c>archiving</c> (RedCompute down, plugin restarted between
/// the phases) is picked up by the reconciler sweep, so a session can never be
/// silently orphaned by an archive.
/// <para>
/// The wait is bounded: a discussion that has been <c>archiving</c> for
/// <see cref="ForceFinalizeAfter"/> (far beyond the client's 5-second close undo, which
/// is resolved before this server intent exists) is finalized even if its session
/// could not be confirmed stopped — otherwise it shows "Finalizing…" forever. Only a
/// session that was reached and still reports a live status is overridden; an
/// unreachable RedCompute never forces anything. Nothing is deleted: the archived
/// discussion keeps its full transcript and can be reopened.
/// </para>
/// </summary>
public sealed class DiscussionLifecycle(
    DiscussionStore store, RedComputeClient redCompute,
    ILogger<DiscussionLifecycle>? logger = null, TimeProvider? clock = null)
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long a discussion may sit in <c>archiving</c> before the finalizer
    /// stops waiting for the session to report a terminal state.</summary>
    public static readonly TimeSpan ForceFinalizeAfter = TimeSpan.FromMinutes(10);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    // When each pending archive was first seen (intent committed or first swept after a
    // restart). In-memory by design: a restart only restarts the bounded wait.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> archivingSince = new();

    /// <summary>Commit the archive intent and kick off background finalization.
    /// Returns the resulting status ("archiving", or "archived" for session-less
    /// discussions), or null if the entity vanished.</summary>
    public async Task<string?> BeginArchiveAsync(DiscussionRead discussion, CancellationToken ct = default)
    {
        var status = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Archiving, ct);
        if (status == null) return null;

        archivingSince.TryAdd(discussion.EntityId, clock.GetUtcNow());
        _ = Task.Run(() => FinalizeAsync(discussion.EntityId, discussion.SessionId));
        return status;
    }

    /// <summary>
    /// Stop the session, verify it no longer runs, then archiving → archived.
    /// Returns true when the discussion reached <c>archived</c>. Safe to call
    /// repeatedly; every failure path leaves the discussion in <c>archiving</c>
    /// for the next sweep, until the bounded wait (<see cref="ForceFinalizeAfter"/>)
    /// runs out and a reachable-but-still-live session no longer blocks finalization.
    /// </summary>
    public async Task<bool> FinalizeAsync(Guid entityId, string? sessionId, CancellationToken ct = default)
    {
        try
        {
            var since = archivingSince.GetOrAdd(entityId, _ => clock.GetUtcNow());
            if (sessionId != null)
            {
                // Issued on every attempt, so the pass that finds the deadline expired
                // has just re-issued the stop once more before deciding.
                try { await redCompute.StopAsync(sessionId, ct); }
                catch { /* verification below decides, not the stop call */ }

                var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
                if (!probe.Reachable) return false;

                // "Stopped"/"Error" and a 404 (Status null) all mean no live process.
                var running = probe.Status is "Starting" or "Active" or "Idle";
                if (running)
                {
                    var waited = clock.GetUtcNow() - since;
                    if (waited < ForceFinalizeAfter) return false;

                    logger?.LogWarning(
                        "Archive of discussion {EntityId} finalized without a confirmed stop: session {SessionId} still {Status} after {Waited:F0} min in archiving",
                        entityId, sessionId, probe.Status, waited.TotalMinutes);
                }
            }

            var finalized = await store.TrySetStatusAsync(entityId, DiscussionStatus.Archived, ct);
            if (finalized != DiscussionStatus.Archived)
            {
                // Entity gone, or no longer archiving: nothing left to track.
                if (finalized == null) archivingSince.TryRemove(entityId, out _);
                return false;
            }

            archivingSince.TryRemove(entityId, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sweep for discussions whose archive never finalized and retry them.
    /// Runs until the host stops; also catches intents from before a restart.</summary>
    public async Task RunReconcilerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                await ReconcilePendingAsync(ct);
            }
            catch { /* next sweep retries */ }
        }
    }

    internal async Task ReconcilePendingAsync(CancellationToken ct = default)
    {
        var pending = await store.ListByStatusAsync(DiscussionStatus.Archiving, ct: ct);
        var pendingIds = pending.Select(d => d.EntityId).ToHashSet();
        foreach (var tracked in archivingSince.Keys)
            if (!pendingIds.Contains(tracked)) archivingSince.TryRemove(tracked, out _);
        foreach (var discussion in pending)
            await FinalizeAsync(discussion.EntityId, discussion.SessionId, ct);
    }
}
