using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Which LIVE timeline events reach a discussion's &lt;nova-context&gt;, decided by the discussion's
/// personal context setting.
/// </summary>
/// <remarks>
/// The LIVE timeline is personal: mail notices, Heartbeat notes meant for LIVE, other instances'
/// agent notes, music, lights. A discussion with personal context sees all of it. That is the
/// default for LIVE and Heartbeat, and any discussion can be switched either way
/// (<see cref="DiscussionRead.PersonalContextSetting"/>), because a person may open an ordinary
/// discussion precisely for life admin. Every other discussion is work: it sees only the
/// operational sources named in <see cref="WorkSources"/>. The list names what is allowed, never
/// what is refused, so a source added later by any extension stays out of work discussions until
/// someone adds it here on purpose.
/// </remarks>
public static class LiveEventScope
{
    /// <summary>How many LIVE events one context lists.</summary>
    public const int MaxEvents = 15;

    /// <summary>The discussion record key holding an explicit choice. Absent: the type's default.</summary>
    public const string DataKey = "personal_context";

    /// <summary>The <c>scope</c> attribute of the context tag.</summary>
    public const string Personal = "personal";
    public const string Work = "work";

    private const string EventPrefix = "event:";

    /// <summary>
    /// The LIVE sources a work discussion may see: automation outcomes, delegation callbacks,
    /// activity in other discussions, presence and device switches. Exact, case-sensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> WorkSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "automation",
        "callback",
        "discussion",
        "presence",
        "device",
    };

    /// <summary>Personal context when nobody chose: on for LIVE and Heartbeat, off for every other type.</summary>
    public static bool DefaultFor(string? discussionType)
        => discussionType is "live" or HeartbeatService.DiscussionType;

    /// <summary>The effective setting: an explicit choice wins over the type's default, both ways.</summary>
    public static bool IsPersonal(DiscussionRead discussion)
        => discussion.PersonalContextSetting ?? DefaultFor(discussion.Type);

    public static string ScopeOf(bool personal) => personal ? Personal : Work;

    /// <summary>
    /// Whether a LIVE record with <paramref name="recordSource"/> (its stored <c>event:*</c> tag) may
    /// reach a discussion with or without personal context. Anything that isn't an event is no.
    /// </summary>
    public static bool Admits(bool personal, string? recordSource)
    {
        if (recordSource is null || !recordSource.StartsWith(EventPrefix, StringComparison.Ordinal))
            return false;
        return personal || WorkSources.Contains(recordSource[EventPrefix.Length..]);
    }

    /// <summary>
    /// The newest <see cref="MaxEvents"/> LIVE events the discussion may see, oldest first. The scope
    /// is applied before the cut, so a work discussion still gets up to <see cref="MaxEvents"/>
    /// allowed events however much personal traffic came after them.
    /// </summary>
    public static List<ContextSnapshot.LiveEventEntry> Select(
        IEnumerable<DiscussionMessage> liveMessages, bool personal)
        => liveMessages
            .Where(m => Admits(personal, m.Metadata["source"]?.GetValue<string>()))
            .TakeLast(MaxEvents)
            .Select(m => new ContextSnapshot.LiveEventEntry
            {
                Source = m.Metadata["source"]!.GetValue<string>(),
                Content = m.Content,
                Timestamp = RecordedAt(m),
            })
            .ToList();

    private static DateTime RecordedAt(DiscussionMessage m)
    {
        var ts = m.Metadata["timestamp"]?.GetValue<string>();
        return ts != null && DateTimeOffset.TryParse(ts, out var t) ? t.UtcDateTime : m.CreatedAt.UtcDateTime;
    }
}
