using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Nova.App.Configuration;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

/// <summary>
/// Syncs Nova's identity/persona files with RedLeaf, where they live as
/// versioned `agent-file` entities (the source of truth — locked decision in
/// redleaf/MIGRATION-PLAN.md). At startup: pull entities and materialize them
/// to disk so AI sessions keep reading plain files; if an entity doesn't
/// exist yet, seed it upward from the local file. Local edits push through
/// <see cref="NovaMirror.PublishAgentFile"/>. RedLeaf being down means Nova
/// just runs on the local copies.
/// </summary>
public static class AgentFileSync
{
    // file key → path relative to the workspace root
    private static readonly (string Key, string Name, string RuntimePath)[] Files =
    [
        ("identity", "Identity", "config/identity.md"),
        ("memory", "Memory Instructions", "config/memory.md"),
        ("output-protocol", "Output Protocol", "config/output_protocol.md"),
        ("capabilities", "Capabilities", "config/capabilities.md"),
    ];

    public static string DisplayName(string fileKey) =>
        Files.FirstOrDefault(f => f.Key == fileKey).Name ?? fileKey;

    /// <summary>The tracked file key for a workspace-relative path, or null.</summary>
    public static string? FileKeyForPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var (key, _, runtimePath) in Files)
            if (string.Equals(normalized, runtimePath, StringComparison.OrdinalIgnoreCase))
                return key;
        return null;
    }

    public static async Task PullOrSeedAsync(MemoryManager memory, NovaConfig config, LogService log)
    {
        try
        {
            var redSuiteDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
            var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
            var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
            var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);

            using var http = new HttpClient
            {
                BaseAddress = new Uri(config.Suite.RedLeaf.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            await EnsureEntityTypeAsync(http, log);

            foreach (var (key, name, runtimePath) in Files)
            {
                var localContent = ReadEffective(memory, key);
                var remote = await FetchAsync(http, $"nova-{key}");

                if (remote is null)
                {
                    if (string.IsNullOrWhiteSpace(localContent)) continue;

                    // First run: seed RedLeaf from the local file.
                    var body = JsonSerializer.Serialize(new
                    {
                        name = $"Nova {name}",
                        type_slug = "agent-file",
                        data = new { app = "nova", file_key = key, content = localContent },
                    });
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    var resp = await http.PutAsync($"api/entities/by-slug/nova-{key}", content);
                    if (resp.IsSuccessStatusCode)
                        log.Info("agent-files", $"Seeded '{key}' into RedLeaf from local file");
                    else
                        log.Warn("agent-files", $"Failed to seed '{key}' into RedLeaf: {(int)resp.StatusCode}");
                }
                else if (remote != localContent)
                {
                    // RedLeaf is the source of truth — materialize to disk.
                    var fullPath = Path.Combine(memory.WorkspacePath, runtimePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, remote);
                    log.Info("agent-files", $"Materialized '{key}' from RedLeaf ({remote.Length} chars)");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("agent-files", $"RedLeaf sync skipped — running on local files: {ex.Message}");
        }
    }

    private static string ReadEffective(MemoryManager memory, string key) => key switch
    {
        "identity" => memory.ReadIdentity(),
        "memory" => memory.ReadMemoryInstructions(),
        "output-protocol" => memory.ReadOutputProtocol(),
        "capabilities" => memory.ReadCapabilities(),
        _ => "",
    };

    private static async Task<string?> FetchAsync(HttpClient http, string slug)
    {
        var resp = await http.GetAsync($"api/entities/{slug}");
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
            return null;

        using var data = JsonDocument.Parse(dataEl.GetString()!);
        return data.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
    }

    private static async Task EnsureEntityTypeAsync(HttpClient http, LogService log)
    {
        var existing = await http.GetAsync("api/entity-types/agent-file");
        if (existing.IsSuccessStatusCode) return;

        // Versioned on purpose: identity edits are exactly the kind of change
        // where history matters.
        var body = JsonSerializer.Serialize(new
        {
            name = "Agent File",
            description = "Identity/persona markdown for an AI agent, materialized to disk by the owning app",
            icon = "fa-solid fa-id-card",
            color = "violet",
            fields = new object[]
            {
                new { name = "App", fieldType = "string", description = "Owning Red Suite app" },
                new { name = "File Key", fieldType = "string", description = "Which agent file this is (identity, memory, output-protocol, capabilities)" },
                new { name = "Content", fieldType = "markdown", description = "The file content" },
            },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("api/entity-types", content);
        if (resp.IsSuccessStatusCode)
            log.Info("agent-files", "Created entity type 'agent-file' in RedLeaf");
        else
            log.Warn("agent-files", $"Failed to create entity type 'agent-file': {(int)resp.StatusCode}");
    }
}
