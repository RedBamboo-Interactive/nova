using System.Text.Json.Nodes;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// A mirror of RedCompute's <c>PlacementParser.TryParseValue</c> plus <c>ResolveNode</c> for the ids a caller
/// may send before nodes exist (redcompute <c>Services/Placement/PlacementRequest.cs</c>, R1 73184e8, nodes as of 1e85423):
/// what RedCompute refuses with 422 (or 404 for a node) before a session or one-shot starts. A caller's
/// placement must pass it, or the work that starts today would fail. Pinned against R1's own cases in
/// <see cref="PlacementRulesTests"/>.
/// </summary>
public static class PlacementRules
{
    public enum Route { Session, Execute }

    private const int MaxTextLength = 512;
    private static readonly string[] TopMembers = ["nodeId", "workspace", "repository", "scratch", "scratchAs"];

    /// <summary>
    /// The member RedCompute would refuse in <paramref name="body"/>'s <c>placement</c>, or null when it starts.
    /// <paramref name="registry"/> is the node ids GET /nodes lists in the test's RedCompute (ResolveNode answers 404
    /// for any other); without one only <c>local</c> is known, as before nodes exist.
    /// </summary>
    public static string? Problem(JsonNode? body, Route route, bool conversationOnly = false,
        IReadOnlyCollection<string>? registry = null)
    {
        if (body is not JsonObject obj || !obj.TryGetPropertyValue("placement", out var value) || value is null) return null;
        if (value is not JsonObject placement) return "placement";

        foreach (var (name, member) in placement)
        {
            if (!TopMembers.Contains(name, StringComparer.Ordinal)) return $"placement.{name}";
            if (member is null || name == "nodeId") continue;
            if (conversationOnly) return $"placement.{name}";
        }

        if (TextProblem(placement, "nodeId", required: false, out var nodeId) is { } nodeProblem) return "placement." + nodeProblem;
        if (ObjectProblem(placement, "workspace", ["agentId"], out var workspace) is { } w) return w;
        if (workspace is not null && GuidProblem(workspace, "agentId", required: true) is { } wa) return "placement.workspace." + wa;
        if (ObjectProblem(placement, "repository", ["repositoryId", "checkoutId", "branch", "subPath"], out var repository) is { } r) return r;
        if (repository is not null)
        {
            if (GuidProblem(repository, "repositoryId", required: true) is { } ri) return "placement.repository." + ri;
            if (GuidProblem(repository, "checkoutId", required: false) is { } ci) return "placement.repository." + ci;
            if (TextProblem(repository, "branch", required: false, out _) is { } b) return "placement.repository." + b;
            if (TextProblem(repository, "subPath", required: false, out var subPath) is { } sp) return "placement.repository." + sp;
            if (subPath is not null && SubPathProblem(subPath)) return "placement.repository.subPath";
        }
        if (ObjectProblem(placement, "scratch", ["owner", "executionKey"], out var scratch) is { } s) return s;
        if (scratch is not null)
        {
            if (TextProblem(scratch, "owner", required: true, out _) is { } o) return "placement.scratch." + o;
            if (TextProblem(scratch, "executionKey", required: true, out _) is { } k) return "placement.scratch." + k;
        }
        if (TextProblem(placement, "scratchAs", required: false, out var scratchAs) is { } sa) return "placement." + sa;

        if (workspace is not null && repository is not null) return "placement";
        if (scratchAs is not null)
        {
            if (scratch is null) return "placement.scratchAs";
            if (scratchAs is not ("env" or "add-dir" or "cwd")) return "placement.scratchAs";
            if (scratchAs == "add-dir" && route != Route.Execute) return "placement.scratchAs";
            if (scratchAs == "cwd" && (workspace is not null || repository is not null)) return "placement.scratchAs";
        }

        // Before Phase 3 nodes, only "local" (any case) and "route" (absent or null) are known on every RedCompute.
        if (nodeId is not null && !string.Equals(nodeId, "local", StringComparison.OrdinalIgnoreCase)
            && registry?.Contains(nodeId) != true) return "placement.nodeId";
        return null;
    }

    private static string? ObjectProblem(JsonObject parent, string name, string[] members, out JsonObject? value)
    {
        value = null;
        if (!parent.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is not JsonObject obj) return $"placement.{name}";
        foreach (var (member, _) in obj)
            if (!members.Contains(member, StringComparer.Ordinal)) return $"placement.{name}.{member}";
        value = obj;
        return null;
    }

    private static string? TextProblem(JsonObject parent, string name, bool required, out string? text)
    {
        text = null;
        if (!parent.TryGetPropertyValue(name, out var node) || node is null) return required ? name : null;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return name;
        if (s.Length > MaxTextLength || s.Any(char.IsControl)) return name;
        text = s;
        return null;
    }

    private static string? GuidProblem(JsonObject parent, string name, bool required)
    {
        if (TextProblem(parent, name, required, out var text) is { } problem) return problem;
        return text is null || Guid.TryParse(text, out _) ? null : name;
    }

    private static bool SubPathProblem(string subPath)
    {
        if (subPath.StartsWith('/') || subPath.StartsWith('\\') || subPath.Contains(':')) return true;
        var segments = subPath.Split('/', '\\');
        return segments.Any(segment => segment is "..") || segments.All(segment => segment.Length == 0 || segment == ".");
    }
}
