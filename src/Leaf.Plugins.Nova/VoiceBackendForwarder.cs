using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>One unsolicited backend message for an open Natural Voice session.</summary>
public sealed record VoiceBackendUpdate(
    string VoiceSessionId,
    string DiscussionId,
    string UpdateId,
    string Mode,
    string Content);

/// <summary>
/// Makes Natural Voice hear what canonical Nova says on her own. While a voice binding is
/// open, every settled backend assistant final that the voice did not cause is delivered
/// to the voice session exactly once, keyed by its messageUid: the newest of a batch is
/// spoken, older ones become context. Delegation results (already spoken through their
/// delegation update), GPT-Live's own persisted speech (<c>source=voice-session</c>) and
/// text-free tool turns are excluded. This is delivery only: nothing is written to the
/// transcript, because the canonical message already exists.
/// </summary>
/// <remarks>
/// Fed by <see cref="ConversationUnread"/>, Nova's existing settled-turn reconciliation,
/// which reads RedCompute's Idle snapshot; Nova's backend does not see the live stream. A
/// voice binding implies the discussion is mounted in a visible browser, whose settled
/// lifecycle drives that reconciliation. It is a standalone singleton so the read path does
/// not depend on <see cref="VoiceSessionService"/>, which itself depends on the pipeline.
/// </remarks>
public sealed class VoiceBackendForwarder(ILogger<VoiceBackendForwarder>? logger = null)
{
    public const string Speak = "speak";
    public const string Context = "context";
    internal const int SpokenMaxChars = 1_200;
    internal const int ContextMaxChars = 1_500;

    internal sealed record BackendFinal(string MessageUid, string Content);

    private sealed class Tracked
    {
        public required string VoiceSessionId { get; init; }
        public required string DiscussionId { get; init; }
        public required string AiSessionId { get; init; }
        public object Lock { get; } = new();
        // Transcript position at admission: the voice already received everything up to
        // here as initial history. Null until a baseline snapshot has been seen.
        public long? Watermark { get; set; }
        public HashSet<string> Delivered { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> PendingDelegationPrompts { get; } = new(StringComparer.Ordinal);
        public bool Closed { get; set; }
    }

    private readonly ConcurrentDictionary<string, Tracked> _sessions = new(StringComparer.Ordinal);
    private Func<VoiceBackendUpdate, CancellationToken, Task<bool>>? _sender;

    public void AttachSender(Func<VoiceBackendUpdate, CancellationToken, Task<bool>> sender)
        => _sender = sender;

    /// <param name="initial">The snapshot the voice session's initial history came from.</param>
    public void Open(string voiceSessionId, string discussionId, string aiSessionId,
        SessionSnapshot? initial)
    {
        var tracked = new Tracked
        {
            VoiceSessionId = voiceSessionId,
            DiscussionId = discussionId,
            AiSessionId = aiSessionId,
            Watermark = initial is null ? null : BaselineWatermark(initial),
        };
        _sessions[voiceSessionId] = tracked;
    }

    public void Close(string voiceSessionId)
    {
        if (_sessions.TryRemove(voiceSessionId, out var tracked))
            lock (tracked.Lock) tracked.Closed = true;
    }

    /// <summary>The turn answering this prompt belongs to the delegation, not to the forwarder.</summary>
    public void DelegationStarted(string voiceSessionId, IEnumerable<string?> promptUids)
    {
        if (!_sessions.TryGetValue(voiceSessionId, out var tracked)) return;
        lock (tracked.Lock)
            foreach (var uid in promptUids.OfType<string>().Distinct(StringComparer.Ordinal))
                tracked.PendingDelegationPrompts[uid] = tracked.PendingDelegationPrompts.GetValueOrDefault(uid) + 1;
    }

    /// <param name="answeredAssistantUids">Turns returned to the delegation; never spoken again.</param>
    public void DelegationFinished(string voiceSessionId, IEnumerable<string?> promptUids,
        IEnumerable<string> answeredAssistantUids)
    {
        if (!_sessions.TryGetValue(voiceSessionId, out var tracked)) return;
        lock (tracked.Lock)
        {
            foreach (var uid in answeredAssistantUids) tracked.Delivered.Add(uid);
            foreach (var uid in promptUids.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!tracked.PendingDelegationPrompts.TryGetValue(uid, out var count)) continue;
                if (count <= 1) tracked.PendingDelegationPrompts.Remove(uid);
                else tracked.PendingDelegationPrompts[uid] = count - 1;
            }
        }
    }

    /// <summary>Called with RedCompute's settled (Idle) snapshot of a discussion's AI session.</summary>
    public async Task OnSettledAsync(string discussionId, string? aiSessionId,
        SessionSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.Status != "Idle" || aiSessionId is null) return;
        foreach (var tracked in _sessions.Values.Where(item =>
                     item.DiscussionId == discussionId && item.AiSessionId == aiSessionId))
        {
            List<BackendFinal> finals;
            lock (tracked.Lock)
            {
                if (tracked.Closed) continue;
                if (tracked.Watermark is null)
                {
                    // No admission baseline: adopt this snapshot as history, speak nothing old.
                    tracked.Watermark = BaselineWatermark(snapshot);
                    continue;
                }
                finals = SelectBackendFinals(snapshot, tracked.Watermark.Value,
                    tracked.Delivered, tracked.PendingDelegationPrompts.Keys.ToHashSet(StringComparer.Ordinal));
                foreach (var final in finals) tracked.Delivered.Add(final.MessageUid);
            }
            for (var index = 0; index < finals.Count; index++)
                await DeliverAsync(tracked, finals[index], index == finals.Count - 1 ? Speak : Context, ct);
        }
    }

    /// <summary>A proactive message posted straight into the discussion (nova-message).</summary>
    public async Task OnDirectAssistantMessageAsync(string discussionId, string messageUid,
        string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        foreach (var tracked in _sessions.Values.Where(item => item.DiscussionId == discussionId))
        {
            lock (tracked.Lock)
                if (tracked.Closed || !tracked.Delivered.Add(messageUid)) continue;
            await DeliverAsync(tracked, new(messageUid, content.Trim()), Speak, ct);
        }
    }

    private async Task DeliverAsync(Tracked tracked, BackendFinal final, string mode, CancellationToken ct)
    {
        var sender = _sender;
        var content = mode == Speak ? PrepareSpokenContent(final.Content) : PrepareContextContent(final.Content);
        var delivered = false;
        if (sender is not null && content.Length > 0)
        {
            try
            {
                delivered = await sender(new(tracked.VoiceSessionId, tracked.DiscussionId,
                    $"assistant:{final.MessageUid}", mode, content), ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Natural Voice backend update {MessageUid} failed for {VoiceSessionId}",
                    final.MessageUid, tracked.VoiceSessionId);
            }
        }
        if (!delivered && content.Length > 0)
            lock (tracked.Lock) tracked.Delivered.Remove(final.MessageUid); // a later settle may retry
    }

    /// <summary>
    /// Assistant turns after <paramref name="watermark"/> that the voice has not received:
    /// not already delivered, not GPT-Live's own persisted speech (<c>voice:</c> message
    /// UIDs, <c>source=voice-session</c>), not the answer to an in-flight delegation, and
    /// with visible text. Content follows <see cref="VoiceSessionService.BuildInitialHistory"/>:
    /// the final phase when present, else non-commentary text.
    /// </summary>
    internal static List<BackendFinal> SelectBackendFinals(SessionSnapshot snapshot, long watermark,
        IReadOnlySet<string> delivered, IReadOnlySet<string> pendingDelegationPrompts)
    {
        var groups = new List<(string Key, string? Prompt, long First, List<SessionMessage> Records)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        string? prompt = null;
        foreach (var message in snapshot.Messages.OrderBy(Position))
        {
            if (message.Role == "user" && message.EventType == "text")
            {
                prompt = message.MessageUid ?? $"record:{message.Id}";
                continue;
            }
            if (message.Role != "assistant") continue;
            var key = message.MessageUid ?? $"record:{message.Id}";
            if (!index.TryGetValue(key, out var position))
            {
                index[key] = position = groups.Count;
                groups.Add((key, prompt, Position(message), []));
            }
            groups[position].Records.Add(message);
        }

        var finals = new List<BackendFinal>();
        foreach (var group in groups)
        {
            if (group.First <= watermark || delivered.Contains(group.Key)
                || group.Key.StartsWith("voice:", StringComparison.Ordinal)
                || group.Prompt is not null && pendingDelegationPrompts.Contains(group.Prompt))
                continue;
            var text = group.Records
                .Where(message => message.EventType == "text" && !string.IsNullOrWhiteSpace(message.Content))
                .ToList();
            var selected = text.Any(message => IsFinalPhase(message.Phase))
                ? text.Where(message => IsFinalPhase(message.Phase))
                : text.Where(message => !string.Equals(message.Phase, "commentary", StringComparison.OrdinalIgnoreCase));
            var content = string.Concat(selected.Select(message => message.Content)).Trim();
            // A silent settle has nothing to say aloud.
            if (content.Length > 0 && !NovaNoReply.IsMarker(content)) finals.Add(new(group.Key, content));
        }
        return finals;
    }

    /// <summary>
    /// Everything up to the newest record was given to the voice as initial history, except
    /// an assistant turn still running at admission: its final has not been heard yet.
    /// </summary>
    internal static long BaselineWatermark(SessionSnapshot snapshot)
    {
        if (snapshot.Messages.Count == 0) return long.MinValue;
        var ordered = snapshot.Messages.OrderBy(Position).ToList();
        var watermark = Position(ordered[^1]);
        if (snapshot.Status == "Idle" || ordered[^1].Role != "assistant") return watermark;
        var running = ordered[^1].MessageUid;
        if (running is null) return watermark;
        return ordered.Where(message => message.MessageUid == running).Min(Position) - 1;
    }

    /// <summary>
    /// GPT-Live paraphrases commentary aloud, so hand it prose: no code, links, paths or
    /// markdown syntax, bounded at a sentence boundary.
    /// </summary>
    internal static string PrepareSpokenContent(string content)
    {
        var text = Regex.Replace(content, @"```.*?(```|\z)", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"`([^`\r\n]*)`", match =>
        {
            var code = match.Groups[1].Value;
            return code.Length > 40 || code.IndexOfAny(['/', '\\', '{', '(', '=', '<']) >= 0 ? " " : code;
        });
        text = Regex.Replace(text, @"https?://\S+", " ");
        text = Regex.Replace(text, @"(?<![\w.])(?:[A-Za-z]:[\\/]|\.{0,2}/)?(?:[\w.-]+[\\/]){2,}[\w.-]*", " ");
        text = Regex.Replace(text, @"(?m)^\s{0,3}(#{1,6}|>|[-*+]|\d+[.)])\s+", "");
        text = Regex.Replace(text, @"(\*\*|__|~~|\*|(?<!\w)_|_(?!\w))", "");
        text = Regex.Replace(text, @"(?m)^\s*\|?[-:| ]+\|?\s*$", " ");
        text = text.Replace('|', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return Bound(text, SpokenMaxChars);
    }

    internal static string PrepareContextContent(string content)
        => Bound(Regex.Replace(content, @"[ \t]+", " ").Trim(), ContextMaxChars);

    private static string Bound(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var window = text[..maxChars];
        var sentence = window.LastIndexOfAny(['.', '!', '?']);
        if (sentence >= maxChars / 2) return window[..(sentence + 1)];
        var space = window.LastIndexOf(' ');
        return (space > maxChars / 2 ? window[..space] : window).TrimEnd() + "...";
    }

    private static long Position(SessionMessage message) => message.Sequence ?? message.Id;

    private static bool IsFinalPhase(string? phase)
        => string.Equals(phase, "final", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase);
}
