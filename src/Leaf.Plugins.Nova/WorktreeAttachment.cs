using System.Text.Json.Nodes;

namespace Leaf.Plugins.Nova;

/// <summary>
/// A worktree (or repository checkout) a discussion works in. The discussion stays in its Agent
/// workspace; this only records where it touches a repository so every turn can say so.
/// </summary>
public sealed record WorktreeAttachment(
    string RepositoryId, string RepositoryName, string Branch, string Path, DateTimeOffset AttachedAt)
{
    public const string DataKey = "worktrees";

    /// <summary>Same folder, ignoring case, separators and a trailing slash.</summary>
    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            static string Normal(string p) => System.IO.Path.GetFullPath(p)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return string.Equals(Normal(left), Normal(right), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static IReadOnlyList<WorktreeAttachment>? Read(JsonObject data)
    {
        if (data[DataKey] is not JsonArray array) return null;
        var items = new List<WorktreeAttachment>();
        foreach (var node in array)
        {
            if (node is not JsonObject o) continue;
            var path = Text(o, "path");
            var branch = Text(o, "branch");
            if (path is null || branch is null) continue;
            items.Add(new WorktreeAttachment(
                Text(o, "repositoryId") ?? "", Text(o, "repositoryName") ?? "", branch, path,
                DateTimeOffset.TryParse(Text(o, "attachedAt"), out var at) ? at : DateTimeOffset.MinValue));
        }
        return items;
    }

    public static JsonArray ToJson(IEnumerable<WorktreeAttachment> items)
        => new(items.Select(a => (JsonNode)new JsonObject
        {
            ["repositoryId"] = a.RepositoryId,
            ["repositoryName"] = a.RepositoryName,
            ["branch"] = a.Branch,
            ["path"] = a.Path,
            ["attachedAt"] = a.AttachedAt.ToString("O"),
        }).ToArray());

    /// <summary>The context line every turn of the discussion carries.</summary>
    public string ContextLine() => $"Working in worktree: {RepositoryName} · {Branch} at {Path}";

    private static string? Text(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
