using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nova.App.Configuration;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

/// <summary>
/// Self-bootstraps Nova as a RedLeaf Agent entity on startup. Creates the
/// agent and its workspace page if they don't exist, syncs identity fields,
/// and backfills the agent ref on existing discussions (once).
/// </summary>
public static class AgentRegistration
{
    private const string AgentSlug = "nova";
    private const string WorkspaceSlug = "nova-workspace";

    private static HttpClient? _http;
    private static LogService? _log;

    /// <summary>
    /// Finds or creates the Nova agent entity in RedLeaf. Returns the entity
    /// ID (GUID string) on success, null if RedLeaf is unreachable.
    /// </summary>
    public static async Task<string?> RegisterAsync(MemoryManager memory, NovaConfig config, LogService log)
    {
        _log = log;
        try
        {
            _http = BuildClient(config);

            var agentId = await FindEntityIdAsync(AgentSlug);
            if (agentId != null)
            {
                log.Info("agent-reg", $"Agent entity found: {agentId}");
                await SyncFieldsAsync(memory);
                await BackfillDiscussionsAsync(agentId, config);
                return agentId;
            }

            // Create workspace page first (agent.workspace is an entity_ref)
            var workspaceId = await FindEntityIdAsync(WorkspaceSlug);
            if (workspaceId == null)
            {
                workspaceId = await CreateEntityAsync(WorkspaceSlug, "page", "Nova Workspace", new
                {
                    description = "Root workspace page for the Nova AI agent",
                });
                if (workspaceId == null)
                {
                    log.Warn("agent-reg", "Failed to create workspace page");
                    return null;
                }
                log.Info("agent-reg", $"Created workspace page: {workspaceId}");
            }

            agentId = await CreateEntityAsync(AgentSlug, "agent", "Nova", new
            {
                description = "AI assistant with persistent identity and memory",
                identity = memory.ReadIdentity(),
                output_protocol = memory.ReadOutputProtocol(),
                capabilities = memory.ReadCapabilities(),
                workspace = workspaceId,
                status = "active",
            });

            if (agentId == null)
            {
                log.Warn("agent-reg", "Failed to create agent entity");
                return null;
            }

            log.Info("agent-reg", $"Created agent entity: {agentId}");
            await BackfillDiscussionsAsync(agentId, config);
            return agentId;
        }
        catch (Exception ex)
        {
            log.Warn("agent-reg", $"Agent registration skipped: {ex.Message}");
            return null;
        }
        finally
        {
            _http?.Dispose();
            _http = null;
        }
    }

    /// <summary>
    /// Syncs local identity/output_protocol/capabilities to the agent entity
    /// if they've changed since last startup.
    /// </summary>
    private static async Task SyncFieldsAsync(MemoryManager memory)
    {
        if (_http == null) return;

        var resp = await _http.GetAsync($"api/entities/{AgentSlug}");
        if (!resp.IsSuccessStatusCode) return;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var data = ParseData(root);
        if (data == null) return;

        var localIdentity = memory.ReadIdentity();
        var localProtocol = memory.ReadOutputProtocol();
        var localCapabilities = memory.ReadCapabilities();

        var remoteIdentity = GetStr(data.Value, "identity");
        var remoteProtocol = GetStr(data.Value, "output_protocol");
        var remoteCapabilities = GetStr(data.Value, "capabilities");

        var avatarValue = GetAvatarValue(data.Value);
        _log?.Info("agent-reg", $"Avatar field raw: {(data.Value.TryGetProperty("avatar", out var rawAv) ? rawAv.ToString() : "(missing)")}");
        if (!string.IsNullOrEmpty(avatarValue))
        {
            var redLeafBase = _http?.BaseAddress?.ToString() ?? "http://127.0.0.1:18804/";
            NovaMirror.AvatarUrl = BuildAvatarUrl(avatarValue, redLeafBase);
            _log?.Info("agent-reg", $"Avatar URL set: {NovaMirror.AvatarUrl}");
        }
        else
        {
            _log?.Info("agent-reg", "No avatar field on agent entity");
        }

        var patch = new Dictionary<string, object?>();
        if (remoteIdentity != localIdentity) patch["identity"] = localIdentity;
        if (remoteProtocol != localProtocol) patch["output_protocol"] = localProtocol;
        if (remoteCapabilities != localCapabilities) patch["capabilities"] = localCapabilities;

        if (patch.Count == 0) return;

        var entityId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (entityId == null) return;

        var body = JsonSerializer.Serialize(patch);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var patchResp = await _http.PatchAsync($"api/entities/{entityId}/data", content);
        if (patchResp.IsSuccessStatusCode)
            _log?.Info("agent-reg", $"Synced {patch.Count} field(s) to agent entity");
        else
            _log?.Warn("agent-reg", $"Failed to sync identity fields: {(int)patchResp.StatusCode}");
    }

    /// <summary>
    /// Resolves the RedLeaf user entity ID for the local user. Returns the ID
    /// of the first user entity found, or null if RedLeaf is unreachable. This
    /// is sufficient for single-user desktop deployments where only one user is
    /// ever registered.
    /// </summary>
    public static async Task<string?> ResolveUserIdAsync(NovaConfig config, LogService log)
    {
        try
        {
            using var http = BuildClient(config);
            var resp = await http.GetAsync("api/entities?type=user&limit=1");
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                {
                    var userId = id.GetString();
                    log.Info("agent-reg", $"Resolved user entity: {userId}");
                    return userId;
                }
            }

            log.Warn("agent-reg", "No user entity found in RedLeaf");
            return null;
        }
        catch (Exception ex)
        {
            log.Warn("agent-reg", $"User ID resolution failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Updates a single field on the agent entity (fire-and-forget from identity editing).
    /// </summary>
    public static async Task UpdateFieldAsync(string fieldName, string value, LogService log)
    {
        try
        {
            using var http = BuildClient(new NovaConfig { Suite = { RedLeaf = "http://127.0.0.1:18804" } });

            var resp = await http.GetAsync($"api/entities/{AgentSlug}");
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var entityId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (entityId == null) return;

            var patch = new Dictionary<string, object?> { [fieldName] = value };
            var body = JsonSerializer.Serialize(patch);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            await http.PatchAsync($"api/entities/{entityId}/data", content);
        }
        catch (Exception ex)
        {
            log.Warn("agent-reg", $"Field sync failed ({fieldName}): {ex.Message}");
        }
    }

    /// <summary>
    /// Backfills the agent ref on existing discussions that lack it. Runs once.
    /// </summary>
    private static async Task BackfillDiscussionsAsync(string agentId, NovaConfig config)
    {
        if (_http == null) return;

        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nova", "migrations", "backfill-discussion-agent");
        if (File.Exists(marker)) return;

        try
        {
            var resp = await _http.GetAsync("api/entities?type=discussion&data.app=nova&limit=500");
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("items");
            var count = 0;

            foreach (var item in items.EnumerateArray())
            {
                var data = ParseData(item);
                if (data == null) continue;

                var existingAgent = GetStr(data.Value, "agent");
                if (!string.IsNullOrEmpty(existingAgent)) continue;

                var slug = item.GetProperty("slug").GetString();
                if (slug == null) continue;

                var updated = new Dictionary<string, object?>();
                foreach (var prop in data.Value.EnumerateObject())
                    updated[prop.Name] = prop.Value.Clone();
                updated["agent"] = agentId;

                var name = item.TryGetProperty("name", out var n) ? n.GetString() : slug;
                var body = JsonSerializer.Serialize(new { name, type_slug = "discussion", data = updated });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var putResp = await _http.PutAsync($"api/entities/by-slug/{slug}", content);
                if (putResp.IsSuccessStatusCode) count++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, $"Backfilled {count} discussions on {DateTime.UtcNow:O}");
            _log?.Info("agent-reg", $"Backfilled {count} discussions with agent ID");
        }
        catch (Exception ex)
        {
            _log?.Warn("agent-reg", $"Discussion backfill failed: {ex.Message}");
        }
    }

    private static HttpClient BuildClient(NovaConfig config)
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);

        var http = new HttpClient
        {
            BaseAddress = new Uri(config.Suite.RedLeaf.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    private static async Task<string?> FindEntityIdAsync(string slug)
    {
        if (_http == null) return null;
        var resp = await _http.GetAsync($"api/entities/{slug}");
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static async Task<string?> CreateEntityAsync(string slug, string typeSlug, string name, object data)
    {
        if (_http == null) return null;
        var body = JsonSerializer.Serialize(new { name, type_slug = typeSlug, data });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"api/entities/by-slug/{slug}", content);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
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

    private static string? GetAvatarValue(JsonElement data)
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

    private static string BuildAvatarUrl(string avatarValue, string redLeafBase)
    {
        var baseUrl = redLeafBase.TrimEnd('/');
        return avatarValue.Contains("://") ? avatarValue
            : avatarValue.StartsWith('/') ? $"{baseUrl}{avatarValue}"
            : $"{baseUrl}/api/assets/{avatarValue}";
    }

    /// <summary>
    /// Fetches the avatar URL from the Nova agent entity. Used as a lazy
    /// fallback when RedLeaf was not reachable at startup.
    /// </summary>
    public static async Task<string?> FetchAvatarUrlAsync(NovaConfig config)
    {
        try
        {
            using var http = BuildClient(config);
            var resp = await http.GetAsync($"api/entities/{AgentSlug}");
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = ParseData(doc.RootElement);
            if (data == null) return null;

            var avatarValue = GetAvatarValue(data.Value);
            return string.IsNullOrEmpty(avatarValue)
                ? null
                : BuildAvatarUrl(avatarValue, config.Suite.RedLeaf);
        }
        catch
        {
            return null;
        }
    }
}
