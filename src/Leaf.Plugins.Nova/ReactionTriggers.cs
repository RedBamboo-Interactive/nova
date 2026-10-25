using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>One reaction change as the endpoint recorded it, with who made it.</summary>
public sealed record ReactionSignal(
    DiscussionRead Discussion, ReactionEntry Entry, bool HumanInitiated, ResolvedDevice Device);

public enum ReactionTriggerOutcome
{
    /// <summary>Not a person's reaction (an Agent's, an automation's), or nowhere to deliver it.</summary>
    Ignored,
    /// <summary>No turn: the next context and the export carry it.</summary>
    Folded,
    /// <summary>Delivered into the running turn.</summary>
    Steered,
    /// <summary>Waiting out the debounce window, which may start one turn.</summary>
    Debounced,
}

/// <summary>
/// When a person's reaction is worth the agent's attention now, and what it costs. Emoji carry no
/// meaning here; only the situation decides:
/// <list type="bullet">
/// <item>the session is mid-turn: one short note steers into that turn, no new turn;</item>
/// <item>idle, on an older message: nothing now, the next context and the export carry it;</item>
/// <item>idle, on the agent's latest message: one turn after <see cref="Debounce"/>, coalescing
/// everything that person changes on that discussion meanwhile; nothing if it nets out.</item>
/// </list>
/// Agent and automation reactions never trigger. Nothing here is human-initiated: no unread,
/// presence or "the owner is typing" semantics change.
/// </summary>
public sealed class ReactionTriggers(
    DiscussionStore store,
    MessagePipeline pipeline,
    RedComputeClient redCompute,
    ILogger<ReactionTriggers> logger)
{
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The debounce wait (tests complete it by hand).</summary>
    internal Func<TimeSpan, Task> Delay { get; set; } = delay => Task.Delay(delay);

    private sealed class Window(ReactionSignal first)
    {
        public readonly List<ReactionEntry> Entries = [first.Entry];
        public ResolvedDevice Device = first.Device;
        public Task Fired = Task.CompletedTask;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(string Discussion, string Actor), Window> _windows = new();

    /// <summary>The pending turn for this person and discussion, when one is waiting (for tests).</summary>
    internal Task? PendingFire(string discussionId, string actorId)
    {
        lock (_gate)
            return _windows.TryGetValue((discussionId, actorId), out var window) ? window.Fired : null;
    }

    public async Task<ReactionTriggerOutcome> OnReactionAsync(ReactionSignal signal, CancellationToken ct = default)
    {
        var discussion = signal.Discussion;
        if (!signal.HumanInitiated || signal.Entry.ActorType != "user"
            || discussion.SessionId is null || discussion.Type == HeartbeatService.DiscussionType
            || DiscussionMutationPolicy.RejectSend(discussion) || DiscussionApplicationOwner.IsForeign(discussion))
            return ReactionTriggerOutcome.Ignored;

        var key = (discussion.Id, signal.Entry.ActorId);
        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var open))
            {
                open.Entries.Add(signal.Entry);
                open.Device = signal.Device;
                return ReactionTriggerOutcome.Debounced;
            }
        }

        var status = await redCompute.GetSessionStatusAsync(discussion.SessionId, ct);
        if (status is "Active" or "Starting")
            return await SteerAsync(discussion, signal.Device, ct);
        // A stopped or parked session stays stopped: a reaction is not a send.
        if (status != "Idle") return ReactionTriggerOutcome.Folded;

        var transcript = await pipeline.LoadReactionTranscriptAsync(
            discussion, new HashSet<string>([signal.Entry.TargetUid], StringComparer.Ordinal), ct);
        if (transcript.LatestAgentUid is null || signal.Entry.TargetUid != transcript.LatestAgentUid)
            return ReactionTriggerOutcome.Folded;

        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var raced))
            {
                raced.Entries.Add(signal.Entry);
                return ReactionTriggerOutcome.Debounced;
            }
            var window = new Window(signal);
            _windows[key] = window;
            window.Fired = FireLaterAsync(key, window);
        }
        return ReactionTriggerOutcome.Debounced;
    }

    private async Task FireLaterAsync((string Discussion, string Actor) key, Window window)
    {
        // The turn belongs to no request: it must not inherit one that has since ended.
        await Task.Yield();
        try
        {
            await Delay(Debounce);
            List<ReactionEntry> entries;
            ResolvedDevice device;
            lock (_gate)
            {
                _windows.Remove(key);
                entries = [.. window.Entries];
                device = window.Device;
            }
            await FireAsync(key.Discussion, entries, device);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reaction turn for discussion {DiscussionId} failed", key.Discussion);
            lock (_gate)
                if (_windows.TryGetValue(key, out var current) && current == window) _windows.Remove(key);
        }
    }

    private async Task FireAsync(string discussionId, List<ReactionEntry> entries, ResolvedDevice device)
    {
        var net = ReactionContext.Net(entries);
        if (net.Count == 0) return;

        var discussion = await store.GetAsync(discussionId);
        if (discussion?.SessionId is null || DiscussionMutationPolicy.RejectSend(discussion)) return;

        // Empty when the person wrote meanwhile: that message's context carried them.
        var notes = await pipeline.GetReactionNotesAsync(discussion, CancellationToken.None);
        if (notes.Lines.Count == 0) return;

        var status = await redCompute.GetSessionStatusAsync(discussion.SessionId);
        if (status is "Active" or "Starting")
        {
            await SendAsync(discussion, notes, device, steer: true);
            return;
        }
        if (status != "Idle") return;
        // Still a change on the agent's latest message once the window settled.
        var latest = notes.Transcript?.LatestAgentUid;
        if (latest is null || !net.Any(entry => entry.TargetUid == latest)) return;
        await SendAsync(discussion, notes, device, steer: false);
    }

    private async Task<ReactionTriggerOutcome> SteerAsync(DiscussionRead discussion, ResolvedDevice device, CancellationToken ct)
    {
        var notes = await pipeline.GetReactionNotesAsync(discussion, ct);
        if (notes.Lines.Count == 0) return ReactionTriggerOutcome.Folded;
        return await SendAsync(discussion, notes, device, steer: true)
            ? ReactionTriggerOutcome.Steered
            : ReactionTriggerOutcome.Folded;
    }

    private async Task<bool> SendAsync(DiscussionRead discussion, MessagePipeline.ReactionNotes notes,
        ResolvedDevice device, bool steer)
    {
        var outcome = await pipeline.SendReactionNoteAsync(discussion, notes, device, steer, CancellationToken.None);
        if (!outcome.Success)
            logger.LogInformation("Reaction note for discussion {DiscussionId} not delivered: {Error}",
                discussion.Id, outcome.ErrorCode);
        return outcome.Success;
    }
}
