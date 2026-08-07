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
    string? WorkspaceId,
    string? Identity,
    string? OutputProtocol,
    string? Capabilities,
    string? MemoryInstructions,
    string? Provider = null,
    string? QualityTier = null);

/// <summary>
/// Cached view of the kernel's <c>agent</c> entities (the agent SYSTEM stays kernel —
/// this only reads it). Also resolves each agent's on-disk workspace path and its
/// effective avatar (outfit override first, base avatar second).
/// </summary>
public sealed class AgentDirectory : IDisposable
{
    private readonly IEntityStore store;
    private readonly IDisposable _avatarChangedSubscription;
    private List<AgentInfo> _agents = [];
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public AgentDirectory(IEntityStore store, IPluginEvents events)
    {
        this.store = store;
        _avatarChangedSubscription = events.Subscribe(
            "agent.avatar-changed",
            _ =>
            {
                _lastRefresh = DateTime.MinValue;
                return Task.CompletedTask;
            });
    }

    /// <summary>Entity id of the Nova agent (slug <c>nova</c>), set at plugin startup.</summary>
    public string? NovaAgentId { get; set; }

    public void Dispose() => _avatarChangedSubscription.Dispose();

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
        var items = await store.QueryAsync(new EntityQuery
        {
            TypeSlug = "agent",
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

            var providerRaw = Str(data, "provider");
            if (providerRaw != null && Guid.TryParse(providerRaw, out var provGuid))
            {
                try { var pe = await store.GetAsync(provGuid, ct); if (pe != null) providerRaw = pe.Slug; }
                catch { }
            }

            string? qualityTier = null;
            var tierRaw = Str(data, "quality_tier");
            if (tierRaw != null && Guid.TryParse(tierRaw, out var tierGuid))
            {
                try
                {
                    var tier = await store.GetAsync(tierGuid, ct);
                    if (tier?.TypeSlug == "quality-tier") qualityTier = tier.Slug;
                }
                catch { }
            }
            else qualityTier = tierRaw;

            // Transitional fallback for databases that have not run the migration yet.
            if (qualityTier == null)
            {
                var legacyModeRaw = Str(data, "quality_mode");
                if (legacyModeRaw != null && Guid.TryParse(legacyModeRaw, out var modeGuid))
                {
                    try
                    {
                        var mode = await store.GetAsync(modeGuid, ct);
                        var legacyTierRaw = mode is null ? null : Str(mode.Data, "quality_tier");
                        if (legacyTierRaw != null && Guid.TryParse(legacyTierRaw, out var legacyTierGuid))
                        {
                            var tier = await store.GetAsync(legacyTierGuid, ct);
                            qualityTier = tier?.TypeSlug == "quality-tier" ? tier.Slug : null;
                        }
                        else
                        {
                            qualityTier = legacyTierRaw;
                        }
                    }
                    catch { }
                }
                else
                {
                    qualityTier = legacyModeRaw;
                }
            }

            agents.Add(new AgentInfo(
                item.Id.ToString(),
                item.Slug,
                item.Name,
                Str(data, "description"),
                avatarFilename,
                workspaceId,
                Str(data, "identity"),
                Str(data, "output_protocol"),
                Str(data, "capabilities"),
                Str(data, "memory_instructions"),
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

    private static string? Str(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }
}
