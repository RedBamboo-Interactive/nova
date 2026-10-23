using System.Globalization;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Agent-facing discussion name. An explicit title always wins. An untitled LIVE
/// is named by its UTC start date (<c>Live · 2026-10-22</c>); when the same Agent
/// has several untitled LIVEs that started that day, the start time is added
/// (<c>Live · 2026-10-22 05:30</c>). Collisions are judged within the listing the
/// caller passes as peers. Any other untitled discussion returns null so each
/// caller keeps its own placeholder.
/// <para>
/// Nothing is stored: archived LIVEs keep <c>type: "live"</c>, so every past LIVE
/// is named on read. The browser applies the same rule in
/// <c>web/src/lib/discussion-display-title.ts</c> with the viewer's locale and
/// time zone, which the server does not have.
/// </para>
/// </summary>
public static class DiscussionDisplayTitle
{
    private const string LiveLabel = "Live";

    public readonly record struct Subject(
        string Id, string? Title, string Type, DateTime? CreatedAt, string? AgentId);

    public static Subject Of(DiscussionRead discussion) => new(
        discussion.Id, discussion.Title, discussion.Type, discussion.CreatedAt, discussion.AgentId);

    public static Subject Of(ContextSnapshot.DiscussionEntry entry) => new(
        entry.Id, entry.Title, entry.Type, entry.CreatedAt, entry.AgentId);

    public static string? For(DiscussionRead discussion, IEnumerable<DiscussionRead>? peers = null)
        => Resolver((peers ?? []).Select(Of))(Of(discussion));

    /// <summary>Resolver for one listing; same-day LIVEs are counted once.</summary>
    public static Func<Subject, string?> Resolver(IEnumerable<Subject> peers)
    {
        var sameDay = new Dictionary<(string Agent, DateOnly Day), HashSet<string>>();
        foreach (var peer in peers)
        {
            if (LiveStart(peer) is not { } start) continue;
            var key = (peer.AgentId ?? "", DateOnly.FromDateTime(start));
            if (!sameDay.TryGetValue(key, out var ids)) sameDay[key] = ids = [];
            ids.Add(peer.Id);
        }

        return subject =>
        {
            if (!string.IsNullOrWhiteSpace(subject.Title)) return subject.Title.Trim();
            if (subject.Type != "live") return null;
            if (LiveStart(subject) is not { } start) return LiveLabel;
            var collides = sameDay.TryGetValue((subject.AgentId ?? "", DateOnly.FromDateTime(start)), out var ids)
                && (ids.Count > 1 || !ids.Contains(subject.Id));
            var format = collides ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
            return $"{LiveLabel} · {start.ToString(format, CultureInfo.InvariantCulture)}";
        };
    }

    private static DateTime? LiveStart(Subject subject)
    {
        if (subject.Type != "live" || !string.IsNullOrWhiteSpace(subject.Title)
            || subject.CreatedAt is not { } created || created == default) return null;
        return created.Kind == DateTimeKind.Local
            ? created.ToUniversalTime()
            : DateTime.SpecifyKind(created, DateTimeKind.Utc);
    }
}
