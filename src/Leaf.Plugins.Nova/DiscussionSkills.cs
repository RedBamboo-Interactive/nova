using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;

namespace Leaf.Plugins.Nova;

/// <summary>
/// A Skill attached to one discussion on demand, beside the Agent's default Skills. The record lives
/// on the discussion entity (<see cref="DataKey"/>), so a restart, a resume or idle parking keeps it,
/// and RedLeaf can read it. <see cref="Kind"/> is <c>skill</c>; the same record will carry MCP servers.
/// </summary>
public sealed record CapabilityAttachment(
    string Kind, string EntityId, string Slug, string Name,
    string AttachedBy, string? AttachedById, string? AttachedByName, DateTimeOffset AttachedAt,
    string? ApprovedById = null)
{
    public const string DataKey = "attached_capabilities";
    public const string SkillKind = "skill";

    /// <summary>Who asked for it: the person (<c>user</c>) or the Agent (<c>agent</c>).</summary>
    public const string ByUser = "user";
    public const string ByAgent = "agent";

    public static IReadOnlyList<CapabilityAttachment>? Read(JsonObject data)
    {
        if (data[DataKey] is not JsonArray array) return null;
        var items = new List<CapabilityAttachment>();
        foreach (var node in array)
        {
            if (node is not JsonObject o) continue;
            var entityId = Text(o, "entityId");
            var slug = Text(o, "slug");
            if (entityId is null || slug is null) continue;
            items.Add(new CapabilityAttachment(
                Text(o, "kind") ?? SkillKind, entityId, slug, Text(o, "name") ?? slug,
                Text(o, "attachedBy") == ByAgent ? ByAgent : ByUser,
                Text(o, "attachedById"), Text(o, "attachedByName"),
                DateTimeOffset.TryParse(Text(o, "attachedAt"), out var at) ? at : DateTimeOffset.MinValue,
                Text(o, "approvedById")));
        }
        return items;
    }

    public static JsonArray ToJson(IEnumerable<CapabilityAttachment> items)
        => new(items.Select(a => (JsonNode)new JsonObject
        {
            ["kind"] = a.Kind,
            ["entityId"] = a.EntityId,
            ["slug"] = a.Slug,
            ["name"] = a.Name,
            ["attachedBy"] = a.AttachedBy,
            ["attachedById"] = a.AttachedById,
            ["attachedByName"] = a.AttachedByName,
            ["attachedAt"] = a.AttachedAt.ToString("O"),
            ["approvedById"] = a.ApprovedById,
        }).ToArray());

    internal static string? Text(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>
/// An Agent's request to attach something its owner did not make attachable. It waits for the
/// person's tap; settled requests stay on the record, newest last, so the card can say how it ended.
/// </summary>
public sealed record CapabilityRequest(
    string Id, string Kind, string EntityId, string Slug, string Name, string? Reason,
    string RequestedById, string? RequestedByName, DateTimeOffset RequestedAt,
    string Status, DateTimeOffset? DecidedAt = null)
{
    public const string DataKey = "capability_requests";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Declined = "declined";

    /// <summary>Settled requests kept for the record; older ones drop off.</summary>
    public const int KeptSettled = 10;

    public bool IsPending => Status == Pending;

    public static IReadOnlyList<CapabilityRequest> Read(JsonObject data)
    {
        if (data[DataKey] is not JsonArray array) return [];
        var items = new List<CapabilityRequest>();
        foreach (var node in array)
        {
            if (node is not JsonObject o) continue;
            var id = CapabilityAttachment.Text(o, "id");
            var entityId = CapabilityAttachment.Text(o, "entityId");
            var slug = CapabilityAttachment.Text(o, "slug");
            if (id is null || entityId is null || slug is null) continue;
            var status = CapabilityAttachment.Text(o, "status");
            items.Add(new CapabilityRequest(id,
                CapabilityAttachment.Text(o, "kind") ?? CapabilityAttachment.SkillKind, entityId, slug,
                CapabilityAttachment.Text(o, "name") ?? slug, CapabilityAttachment.Text(o, "reason"),
                CapabilityAttachment.Text(o, "requestedById") ?? "", CapabilityAttachment.Text(o, "requestedByName"),
                DateTimeOffset.TryParse(CapabilityAttachment.Text(o, "requestedAt"), out var at) ? at : DateTimeOffset.MinValue,
                status is Approved or Declined ? status : Pending,
                DateTimeOffset.TryParse(CapabilityAttachment.Text(o, "decidedAt"), out var decided) ? decided : null));
        }
        return items;
    }

    public static JsonArray ToJson(IEnumerable<CapabilityRequest> items)
    {
        var list = items.ToList();
        // Every pending request stays; of the settled ones, only the newest few.
        var settledToDrop = Math.Max(0, list.Count(r => !r.IsPending) - KeptSettled);
        var kept = list.Where(r => r.IsPending || settledToDrop-- <= 0).ToList();
        return new(kept.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["kind"] = r.Kind,
            ["entityId"] = r.EntityId,
            ["slug"] = r.Slug,
            ["name"] = r.Name,
            ["reason"] = r.Reason,
            ["requestedById"] = r.RequestedById,
            ["requestedByName"] = r.RequestedByName,
            ["requestedAt"] = r.RequestedAt.ToString("O"),
            ["status"] = r.Status,
            ["decidedAt"] = r.DecidedAt?.ToString("O"),
        }).ToArray());
    }
}

/// <summary>A library Skill as the attach surfaces show it.</summary>
public sealed record SkillSummary(string EntityId, string Slug, string Name, string Description);

/// <summary>
/// Reads <c>agent-skill</c> entities and writes a discussion's attached Skills into its scratch folder,
/// in the same Agent Skills layout RedLeaf projects default Skills in (<c>SKILL.md</c> plus package
/// files), so the Agent opens an attached Skill exactly as it opens any other.
/// </summary>
public static class DiscussionSkills
{
    public const string SkillType = "agent-skill";

    /// <summary>The Agent field of Skills it may attach to a discussion itself, without asking.</summary>
    public const string OptionalField = "optional_skills";

    /// <summary>The Agent field of Skills every one of its discussions has.</summary>
    public const string DefaultField = "skills";

    /// <summary>Scratch subfolder holding attached Skills, one folder per slug.</summary>
    public const string FolderName = "attached-skills";

    private const string FingerprintFile = ".leaf-skill-revision";

    private static readonly JsonSerializerOptions YamlJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static SkillSummary Summary(LeafEntity skill)
        => new(skill.Id.ToString(), skill.Slug, string.IsNullOrWhiteSpace(skill.Name) ? skill.Slug : skill.Name,
            Text(skill.Data, "description").Trim());

    /// <summary>The references (ids or slugs) an Agent field lists.</summary>
    public static IReadOnlyList<string> References(JsonObject agentData, string field)
        => agentData[field] is JsonArray list
            ? list.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text?.Trim() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToList()
            : [];

    /// <summary>Whether <paramref name="skill"/> is one of <paramref name="references"/>, by id or slug.</summary>
    public static bool Lists(IReadOnlyList<string> references, LeafEntity skill)
        => references.Any(reference =>
            string.Equals(reference, skill.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(reference, skill.Slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Skill's SKILL.md, compiled as RedLeaf compiles default Skills: frontmatter, then the body.</summary>
    public static string CompileSkillMarkdown(LeafEntity skill)
    {
        var body = Text(skill.Data, "instructions").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var result = new StringBuilder()
            .Append("---\n")
            .Append("name: ").Append(JsonSerializer.Serialize(skill.Slug, YamlJson)).Append('\n')
            .Append("description: ").Append(JsonSerializer.Serialize(Text(skill.Data, "description").Trim(), YamlJson)).Append('\n')
            .Append("---\n\n");
        if (body.Length > 0) result.Append(body).Append('\n');
        return result.ToString();
    }

    /// <summary>The folder an attached Skill is written to inside a discussion's scratch folder.</summary>
    public static string SkillFolder(string scratchRoot, string slug)
        => Path.Combine(scratchRoot, FolderName, SafeSegment(slug));

    public static string SkillFile(string scratchRoot, string slug)
        => Path.Combine(SkillFolder(scratchRoot, slug), "SKILL.md");

    /// <summary>
    /// Writes the Skill into <paramref name="scratchRoot"/> when it is missing or older than the entity.
    /// Scratch may be cleared after inactivity, so every turn and session start calls this; an
    /// up-to-date copy costs one small file read. Returns the SKILL.md path.
    /// </summary>
    public static async Task<string> MaterializeAsync(LeafEntity skill, string scratchRoot,
        Func<string, CancellationToken, Task<byte[]?>> readAsset, CancellationToken ct = default)
    {
        var folder = SkillFolder(scratchRoot, skill.Slug);
        var skillFile = Path.Combine(folder, "SKILL.md");
        var revision = skill.UpdatedAt.UtcTicks.ToString();
        var marker = Path.Combine(folder, FingerprintFile);
        if (File.Exists(skillFile) && File.Exists(marker)
            && (await File.ReadAllTextAsync(marker, ct)).Trim() == revision)
            return skillFile;

        // Rewritten whole, so a file the Skill no longer has does not linger.
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        await File.WriteAllTextAsync(skillFile, CompileSkillMarkdown(skill), Utf8NoBom, ct);

        if (skill.Data["files"] is JsonArray files)
        {
            foreach (var file in files.OfType<JsonObject>())
            {
                var relative = Text(file, "path").Trim().Replace('\\', '/');
                if (relative.Length == 0 || relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(folder, relative));
                // RedLeaf validates package paths; this is the last guard against one leaving the folder.
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!string.Equals(Text(file, "kind"), "asset", StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllTextAsync(target, Text(file, "content"), Utf8NoBom, ct);
                    continue;
                }
                var assetId = AssetId(Text(file, "asset_url"));
                var bytes = assetId is null ? null : await readAsset(assetId, ct);
                if (bytes is not null) await File.WriteAllBytesAsync(target, bytes, ct);
            }
        }
        await File.WriteAllTextAsync(marker, revision, ct);
        return skillFile;
    }

    /// <summary>Removes a detached Skill's copy. Only this folder is touched; the Skill entity stays.</summary>
    public static void Remove(string scratchRoot, string slug)
    {
        var folder = SkillFolder(scratchRoot, slug);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The note the Agent receives when a Skill is attached: the whole SKILL.md, to act on now.</summary>
    public static string AttachedNote(CapabilityAttachment attachment, LeafEntity skill, string skillFile, string by)
        => $"""
            {by} attached the Skill "{attachment.Name}" ({attachment.Slug}) to this discussion. Follow it here from now on, as you follow your default Skills. It applies to this discussion only. Its package is at {Path.GetDirectoryName(skillFile)!.Replace('\\', '/')}; every turn names it, so reopen SKILL.md there if this note is no longer in your context.

            {CompileSkillMarkdown(skill).TrimEnd()}
            """;

    public static string DetachedNote(CapabilityAttachment attachment, string by)
        => $"{by} detached the Skill \"{attachment.Name}\" ({attachment.Slug}) from this discussion. Stop following it here, unless it is also one of your default Skills.";

    public static string DeclinedNote(CapabilityRequest request, string by)
        => $"{by} declined your request to attach the Skill \"{request.Name}\" ({request.Slug}) to this discussion. Carry on without it, and do not ask again for it here unless {by} raises it.";

    /// <summary>The line every turn carries for each attached Skill, so compaction cannot drop it.</summary>
    public static string ContextLine(CapabilityAttachment attachment, string scratchRoot)
        => $"Attached Skill: {attachment.Name} ({attachment.Slug}), at {SkillFile(scratchRoot, attachment.Slug).Replace('\\', '/')}. Follow it in this discussion.";

    /// <summary>The stored asset id of a Skill package file's <c>/api/assets/{id}</c> URL.</summary>
    internal static string? AssetId(string? assetUrl)
    {
        const string prefix = "/api/assets/";
        if (string.IsNullOrWhiteSpace(assetUrl) || !assetUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = Uri.UnescapeDataString(assetUrl[prefix.Length..]);
        return id.Length == 0 || id != Path.GetFileName(id) ? null : id;
    }

    private static string SafeSegment(string slug)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(slug.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c).ToArray()).Trim('.', ' ');
        return clean.Length == 0 ? "skill" : clean;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? "" : "";
}
