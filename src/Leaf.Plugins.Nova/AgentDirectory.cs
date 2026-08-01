using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed record AgentInfo(
    string Id,
    string Slug,
    string Name,
    string? Description,
    string? AvatarFilename,
    string WorkspacePath,
    string? WorkspaceId,
    string? Identity,
    string? OutputProtocol,
    string? Capabilities,
    string? MemoryInstructions,
    string Status,
    string? Provider = null,
    string? QualityMode = null);

/// <summary>
/// Cached view of the kernel's <c>agent</c> entities (the agent SYSTEM stays kernel —
/// this only reads it). Also resolves each agent's on-disk workspace path and its
/// effective avatar (outfit override first, base avatar second).
/// </summary>
public sealed class AgentDirectory(IEntityStore store, NovaConfigStore config)
{
    private List<AgentInfo> _agents = [];
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Entity id of the Nova agent (slug <c>nova</c>), set at plugin startup.</summary>
    public string? NovaAgentId { get; set; }

    public async Task<List<AgentInfo>> GetAgentsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastRefresh < CacheTtl && _agents.Count > 0)
            return _agents;

        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && DateTime.UtcNow - _lastRefresh < CacheTtl && _agents.Count > 0)
                return _agents;

            _agents = await FetchAgentsAsync(ct);
            _lastRefresh = DateTime.UtcNow;
            return _agents;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentInfo?> GetAgentAsync(string agentId, CancellationToken ct = default)
    {
        var agents = await GetAgentsAsync(ct: ct);
        return agents.FirstOrDefault(a => a.Id == agentId);
    }

    public async Task<string?> GetAgentNameAsync(string agentId, CancellationToken ct = default)
        => (await GetAgentAsync(agentId, ct))?.Name;

    public async Task<string?> GetAgentProviderAsync(string agentId, CancellationToken ct = default)
        => (await GetAgentAsync(agentId, ct))?.Provider;

    /// <summary>Same-origin URL for an avatar asset (no proxying needed anymore).</summary>
    public static string? BuildAvatarUrl(string? avatarFilename)
    {
        if (string.IsNullOrEmpty(avatarFilename)) return null;
        return avatarFilename.Contains("://") || avatarFilename.StartsWith('/')
            ? avatarFilename
            : $"/api/assets/{avatarFilename}";
    }

    private async Task<List<AgentInfo>> FetchAgentsAsync(CancellationToken ct)
    {
        var novaWorkspaceOverride = (await SafeConfigAsync(ct))?.WorkspacePath;
        var items = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = "agent",
            DataEquals = new Dictionary<string, object?> { ["status"] = "active" },
            Limit = 50,
        }, ct);

        var agents = new List<AgentInfo>();
        foreach (var item in items)
        {
            var data = item.Data;
            var avatarFilename = await ResolveAvatarAsync(data, ct);

            var workspaceRef = Str(data, "workspace");
            LeafEntity? workspace = null;
            if (!string.IsNullOrWhiteSpace(workspaceRef))
            {
                workspace = Guid.TryParse(workspaceRef, out var workspaceGuid)
                    ? await store.GetAsync(workspaceGuid, ct)
                    : await store.GetBySlugAsync(workspaceRef, ct);
            }
            var workspaceId = workspace?.TypeSlug == "page" ? workspace.Id.ToString() : null;

            var workspacePath = item.Slug == "nova" && novaWorkspaceOverride != null
                ? novaWorkspaceOverride
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RedLeaf", "agents", item.Slug);

            var providerRaw = Str(data, "provider");
            if (providerRaw != null && Guid.TryParse(providerRaw, out var provGuid))
            {
                try { var pe = await store.GetAsync(provGuid, ct); if (pe != null) providerRaw = pe.Slug; }
                catch { }
            }

            string? qualityTier = null;
            var qmRaw = Str(data, "quality_mode");
            if (qmRaw != null && Guid.TryParse(qmRaw, out var qmGuid))
            {
                try { var qe = await store.GetAsync(qmGuid, ct); if (qe != null) qualityTier = Str(qe.Data, "quality_tier"); }
                catch { }
            }
            else qualityTier = qmRaw;

            agents.Add(new AgentInfo(
                item.Id.ToString(),
                item.Slug,
                item.Name,
                Str(data, "description"),
                avatarFilename,
                workspacePath,
                workspaceId,
                Str(data, "identity"),
                Str(data, "output_protocol"),
                Str(data, "capabilities"),
                Str(data, "memory_instructions"),
                Str(data, "status") ?? "active",
                providerRaw,
                qualityTier));
        }
        return agents;
    }

    private async Task<string?> ResolveAvatarAsync(JsonObject data, CancellationToken ct)
    {
        // Outfit override wins; resolution is only attempted when the value looks like
        // an entity id, so a bare base-avatar path is never mistaken for one.
        var outfitId = Str(data, "outfit");
        if (!string.IsNullOrEmpty(outfitId) && !outfitId.StartsWith('/') && !outfitId.Contains("://"))
        {
            if (Guid.TryParse(outfitId, out var outfitGuid))
            {
                try
                {
                    var outfit = await store.GetAsync(outfitGuid, ct);
                    var asset = outfit != null ? Str(outfit.Data, "asset") : null;
                    if (asset != null) return asset;
                }
                catch { }
            }
        }
        else if (!string.IsNullOrEmpty(outfitId))
        {
            return outfitId; // already a URL/path
        }

        if (Str(data, "avatar") is { } baseAvatar) return baseAvatar;
        if (data["avatar"] is JsonObject obj)
            return Str(obj, "filename") ?? Str(obj, "url");
        return null;
    }

    private async Task<NovaAppConfig?> SafeConfigAsync(CancellationToken ct)
    {
        try { return await config.GetAsync(ct); }
        catch { return null; }
    }

    private static string? Str(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }
}
