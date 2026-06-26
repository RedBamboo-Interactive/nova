using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Nova.App.Configuration;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public record AgentInfo(
    string Id,
    string Slug,
    string Name,
    string? Description,
    string? AvatarFilename,
    string WorkspacePath,
    string? Identity,
    string? OutputProtocol,
    string? Capabilities,
    string? MemoryInstructions,
    string Status,
    string? Provider = null);

public class AgentResolver
{
    private readonly NovaConfig _config;
    private readonly LogService _log;
    private List<AgentInfo> _agents = [];
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public AgentResolver(NovaConfig config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public async Task<List<AgentInfo>> GetAgentsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastRefresh < CacheTtl && _agents.Count > 0)
            return _agents;

        await _lock.WaitAsync();
        try
        {
            if (!forceRefresh && DateTime.UtcNow - _lastRefresh < CacheTtl && _agents.Count > 0)
                return _agents;

            var agents = await FetchAgentsAsync();
            _agents = agents;
            _lastRefresh = DateTime.UtcNow;
            return _agents;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentInfo?> GetAgentAsync(string agentId)
    {
        var agents = await GetAgentsAsync();
        return agents.FirstOrDefault(a => a.Id == agentId);
    }

    public string ResolveWorkspacePath(string? agentId, string? agentSlug)
    {
        if (agentId == null || agentId == _config.AgentId)
            return _config.WorkspacePath;

        var slug = agentSlug ?? "unknown";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedLeaf", "agents", slug);
    }

    private async Task<List<AgentInfo>> FetchAgentsAsync()
    {
        try
        {
            using var http = BuildClient();
            var resp = await http.GetAsync("api/entities?type=agent&data.status=active&limit=50");
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warn("agent-resolver", $"Failed to fetch agents: {(int)resp.StatusCode}");
                return BuildFallback();
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            var agents = new List<AgentInfo>();

            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var slug = item.TryGetProperty("slug", out var s) ? s.GetString() : null;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : slug;
                if (id == null || slug == null || name == null) continue;

                var data = ParseData(item);

                var description = data != null ? GetStr(data.Value, "description") : null;
                var identity = data != null ? GetStr(data.Value, "identity") : null;
                var outputProtocol = data != null ? GetStr(data.Value, "output_protocol") : null;
                var capabilities = data != null ? GetStr(data.Value, "capabilities") : null;
                var memoryInstructions = data != null ? GetStr(data.Value, "memory_instructions") : null;
                var status = data != null ? GetStr(data.Value, "status") ?? "active" : "active";
                // Resolve avatar: prefer outfit entity override, fall back to base avatar field.
                // Outfit resolution is attempted only when the outfit field is explicitly set,
                // so a bare base-avatar filename is never mistaken for an entity ID.
                string? avatarFilename = null;
                var outfitId = data != null ? GetStr(data.Value, "outfit") : null;
                if (!string.IsNullOrEmpty(outfitId) && !outfitId.StartsWith('/') && !outfitId.Contains("://"))
                {
                    try
                    {
                        var outfitResp = await http.GetStringAsync($"api/entities/{outfitId}");
                        using var outfitDoc = JsonDocument.Parse(outfitResp);
                        var outfitData = ParseData(outfitDoc.RootElement);
                        avatarFilename = outfitData != null ? GetStr(outfitData.Value, "asset") : null;
                    }
                    catch { }
                }
                else if (!string.IsNullOrEmpty(outfitId))
                {
                    avatarFilename = outfitId; // outfit is already a URL/path
                }
                // Fall back to base avatar if outfit not set or resolution failed
                if (avatarFilename == null)
                    avatarFilename = data != null ? GetBaseAvatar(data.Value) : null;
                var provider = data != null ? GetStr(data.Value, "provider") : null;

                var workspacePath = ResolveWorkspacePath(id, slug);

                agents.Add(new AgentInfo(
                    id, slug, name, description, avatarFilename, workspacePath,
                    identity, outputProtocol, capabilities, memoryInstructions, status, provider));
            }

            if (agents.Count == 0)
                return BuildFallback();

            _log.Info("agent-resolver", $"Loaded {agents.Count} agents from RedLeaf");
            return agents;
        }
        catch (Exception ex)
        {
            _log.Warn("agent-resolver", $"Agent fetch failed: {ex.Message}");
            return BuildFallback();
        }
    }

    private List<AgentInfo> BuildFallback()
    {
        return
        [
            new AgentInfo(
                _config.AgentId ?? "local",
                "nova",
                "Nova",
                "AI assistant with persistent identity and memory",
                null,
                _config.WorkspacePath,
                null, null, null, null,
                "active")
        ];
    }

    private HttpClient BuildClient()
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);

        var http = new HttpClient
        {
            BaseAddress = new Uri(_config.Suite.RedLeaf.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    public string? BuildAvatarUrl(string? avatarFilename)
    {
        if (string.IsNullOrEmpty(avatarFilename)) return null;
        var baseUrl = _config.Suite.RedLeaf.TrimEnd('/');
        return avatarFilename.Contains("://") ? avatarFilename
            : avatarFilename.StartsWith('/') ? $"{baseUrl}{avatarFilename}"
            : $"{baseUrl}/api/assets/{avatarFilename}";
    }

    private static JsonElement? ParseData(JsonElement entity)
    {
        if (!entity.TryGetProperty("data", out var dataEl)) return null;
        if (dataEl.ValueKind == JsonValueKind.Object) return dataEl;
        if (dataEl.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(dataEl.GetString()!);
            return parsed.RootElement.Clone();
        }
        return null;
    }

    private static string? GetStr(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static string? GetBaseAvatar(JsonElement data)
    {
        if (!data.TryGetProperty("avatar", out var av)) return null;
        if (av.ValueKind == JsonValueKind.String) return av.GetString();
        if (av.ValueKind == JsonValueKind.Object)
        {
            if (av.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                return fn.GetString();
            if (av.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        }
        return null;
    }
}
