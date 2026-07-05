namespace Leaf.Plugins.Nova;

/// <summary>
/// Two-phase archive. Phase one commits the durable intent (<c>archiving</c>) and
/// returns immediately — from that moment the discussion is closed to every status
/// writer and hidden from lists. Phase two, in the background, stops the RedCompute
/// session, verifies it is actually gone, and only then finalizes to <c>archived</c>.
/// A discussion stuck in <c>archiving</c> (RedCompute down, plugin restarted between
/// the phases) is picked up by the reconciler sweep, so a session can never be
/// silently orphaned by an archive.
/// </summary>
public sealed class DiscussionLifecycle(DiscussionStore store, RedComputeClient redCompute)
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>Commit the archive intent and kick off background finalization.
    /// Returns the resulting status ("archiving", or "archived" for session-less
    /// discussions), or null if the entity vanished.</summary>
    public async Task<string?> BeginArchiveAsync(DiscussionRead discussion, CancellationToken ct = default)
    {
        var status = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Archiving, ct);
        if (status == null) return null;

        _ = Task.Run(() => FinalizeAsync(discussion.EntityId, discussion.SessionId));
        return status;
    }

    /// <summary>
    /// Stop the session, verify it no longer runs, then archiving → archived.
    /// Returns true when the discussion reached <c>archived</c>. Safe to call
    /// repeatedly; every failure path leaves the discussion in <c>archiving</c>
    /// for the next sweep.
    /// </summary>
    public async Task<bool> FinalizeAsync(Guid entityId, string? sessionId, CancellationToken ct = default)
    {
        try
        {
            if (sessionId != null)
            {
                try { await redCompute.StopAsync(sessionId, ct); }
                catch { /* verification below decides, not the stop call */ }

                var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
                if (!probe.Reachable) return false;

                // "Stopped"/"Error" and a 404 (Status null) all mean no live process.
                var running = probe.Status is "Starting" or "Active" or "Idle";
                if (running) return false;
            }

            return await store.TrySetStatusAsync(entityId, DiscussionStatus.Archived, ct)
                == DiscussionStatus.Archived;
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
                var pending = (await store.ListAsync(ct: ct))
                    .Where(d => d.Status == DiscussionStatus.Archiving)
                    .ToList();
                foreach (var d in pending)
                    await FinalizeAsync(d.EntityId, d.SessionId, ct);
            }
            catch { /* next sweep retries */ }
        }
    }
}
