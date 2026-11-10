using System.Globalization;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Whether a discussion's &lt;nova-context&gt; carries the LIVE timeline, and the timeline itself.
/// </summary>
/// <remarks>
/// One switch per discussion, "LIVE timeline". On, the context lists the newest
/// <see cref="MaxEvents"/> LIVE events whatever their source; off, it lists none and the full
/// context instead says how to read them (<c>GET /live/timeline</c>). Nobody choosing means on for
/// LIVE and Heartbeat and off for every other type; an explicit value wins both ways
/// (<see cref="DiscussionRead.LiveTimelineSetting"/>).
/// </remarks>
public static class LiveTimelineContext
{
    /// <summary>How many LIVE events one context lists, and the timeline route's default page.</summary>
    public const int MaxEvents = 15;

    /// <summary>The largest page the timeline route returns.</summary>
    public const int MaxPage = 100;

    /// <summary>The discussion record key holding an explicit choice. Absent: the type's default.</summary>
    public const string DataKey = "live_timeline";

    /// <summary>
    /// Where the same choice was stored before the setting had its name. Read only, as a fallback
    /// when <see cref="DataKey"/> is absent; never written.
    /// </summary>
    public const string LegacyDataKey = "personal_context";

    private const string EventPrefix = "event:";

    /// <summary>The setting when nobody chose: on for LIVE and Heartbeat, off for every other type.</summary>
    public static bool DefaultFor(string? discussionType)
        => discussionType is "live" or HeartbeatService.DiscussionType;

    /// <summary>The effective setting: an explicit choice wins over the type's default, both ways.</summary>
    public static bool IsOn(DiscussionRead discussion)
        => discussion.LiveTimelineSetting ?? DefaultFor(discussion.Type);

    /// <summary>
    /// What a context holds: with the timeline on, the newest <see cref="MaxEvents"/> LIVE events,
    /// oldest first, from every source; off, none.
    /// </summary>
    public static List<ContextSnapshot.LiveEventEntry> Select(IEnumerable<DiscussionMessage> liveMessages, bool on)
        => on ? Events(liveMessages).TakeLast(MaxEvents).ToList() : [];

    /// <summary>
    /// The LIVE records that are events (stored source <c>event:*</c>), oldest first, each with the
    /// text a context line shows and the time it happened.
    /// </summary>
    public static IEnumerable<ContextSnapshot.LiveEventEntry> Events(IEnumerable<DiscussionMessage> liveMessages)
        => liveMessages
            .Where(m => m.Metadata["source"]?.GetValue<string>() is { } source
                && source.StartsWith(EventPrefix, StringComparison.Ordinal))
            .Select(m => new ContextSnapshot.LiveEventEntry
            {
                Source = m.Metadata["source"]!.GetValue<string>(),
                Content = m.Content,
                Timestamp = RecordedAt(m),
            });

    /// <summary>A source as a context line names it: <c>event:mail</c> is <c>mail</c>.</summary>
    public static string SourceName(string source)
        => source.StartsWith(EventPrefix, StringComparison.Ordinal) ? source[EventPrefix.Length..] : source;

    /// <summary>
    /// One page of the timeline, newest last: the latest <paramref name="limit"/> events strictly
    /// after <paramref name="since"/> and strictly before <paramref name="before"/>, of
    /// <paramref name="source"/> only when given (as a context line names it). <c>HasMore</c> says
    /// older matching events exist; pass the first event's timestamp as <c>before</c> to get them.
    /// </summary>
    public static (List<ContextSnapshot.LiveEventEntry> Events, bool HasMore) Page(
        IEnumerable<DiscussionMessage> liveMessages, int limit, DateTime? before, DateTime? since, string? source)
    {
        var matching = Events(liveMessages)
            .Where(e => before is not { } b || e.Timestamp < b)
            .Where(e => since is not { } s || e.Timestamp > s)
            .Where(e => string.IsNullOrEmpty(source) || string.Equals(SourceName(e.Source), source, StringComparison.Ordinal))
            .OrderBy(e => e.Timestamp)
            .ToList();
        var page = matching.TakeLast(limit).ToList();
        return (page, matching.Count > page.Count);
    }

    /// <summary>A query timestamp: ISO 8601, read as UTC when it names no offset.</summary>
    public static bool TryParseTime(string? value, out DateTime? time)
    {
        time = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return false;
        time = t.UtcDateTime;
        return true;
    }

    private static DateTime RecordedAt(DiscussionMessage m)
    {
        var ts = m.Metadata["timestamp"]?.GetValue<string>();
        return ts != null && DateTimeOffset.TryParse(ts, out var t) ? t.UtcDateTime : m.CreatedAt.UtcDateTime;
    }
}
