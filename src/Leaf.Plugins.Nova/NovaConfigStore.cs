using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed record NovaAppConfig(
    Guid EntityId,
    string? DockerImage,
    string DefaultQualityMode,
    string? WorkspacePath);

/// <summary>
/// Nova app settings on the <c>nova-config</c> singleton entity. Distinct slug
/// (<c>nova-app-config-singleton</c>) and type-scoped lookup — the bare
/// <c>&lt;id&gt;-config</c> slug pattern collides with the entity-type row.
/// Created in OnStartupAsync, never seeded (seeds re-upsert and would clobber edits).
/// </summary>
public sealed class NovaConfigStore(IEntityStore store)
{
    public const string TypeSlug = "nova-config";
    public const string Slug = "nova-app-config-singleton";

    public async Task<NovaAppConfig> GetAsync(CancellationToken ct = default)
    {
        var entities = await store.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = TypeSlug, Limit = 1 }, ct);
        var e = entities.FirstOrDefault()
            ?? throw new InvalidOperationException("nova-config singleton is missing — OnStartupAsync did not run");
        return new NovaAppConfig(
            e.Id,
            Str(e.Data, "docker_image"),
            Str(e.Data, "default_quality_mode") ?? "standard",
            Str(e.Data, "workspace_path"));
    }

    public async Task PatchAsync(JsonObject patch, CancellationToken ct = default)
    {
        var config = await GetAsync(ct);
        await store.PatchAsync(config.EntityId, patch, ct: ct);
    }

    /// <summary>Raw data object, for keys outside the typed view (e.g. Steam credentials).</summary>
    public async Task<JsonObject> GetRawAsync(CancellationToken ct = default)
    {
        var entities = await store.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = TypeSlug, Limit = 1 }, ct);
        return entities.FirstOrDefault()?.Data ?? [];
    }

    private static string? Str(JsonObject data, string key)
    {
        var v = data[key]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
