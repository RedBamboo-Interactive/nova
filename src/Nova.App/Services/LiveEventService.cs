using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nova.App.Data;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class LiveEventService
{
    public static LiveEventService? Instance { get; set; }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LogService _log;
    private string? _cachedLiveId;

    public LiveEventService(IServiceScopeFactory scopeFactory, LogService log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        Instance = this;
        _log.Info("live-events", $"LiveEventService initialized (Instance set: {Instance != null})");
    }

    private HttpClient BuildNovaClient()
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        var token = jwt.GenerateAccessToken("local-user", "local@nova", "Nova System", ["admin"]);
        var http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:18803/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    public async Task PostAsync(string source, string content, object? metadata = null)
    {
        var liveId = await ResolveLiveIdAsync();
        if (liveId == null) { _log.Warn("live-events", "Cannot post: LIVE discussion ID not resolved"); return; }

        try
        {
            using var http = BuildNovaClient();
            var body = metadata != null
                ? new { content, source, type = "system", metadata }
                : (object)new { content, source, type = "system" };

            var resp = await http.PostAsJsonAsync($"api/discussions/{liveId}/event", body);
            if (resp.IsSuccessStatusCode)
                _log.Info("live-events", $"Posted ({source}): {content[..Math.Min(content.Length, 60)]}");
            else
                _log.Warn("live-events", $"Event POST failed ({source}): HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex)
        {
            _log.Warn("live-events", $"Failed to post event ({source}): {ex.Message}");
        }
    }

    public string? DiscussionId => _cachedLiveId;

    public void InvalidateCache() => _cachedLiveId = null;

    private async Task<string?> ResolveLiveIdAsync()
    {
        if (_cachedLiveId != null) return _cachedLiveId;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaDbContext>();
            _cachedLiveId = await db.Discussions
                .Where(d => d.Type == "live" && d.AgentId == NovaMirror.AgentId && d.Status != "archived")
                .Select(d => d.Id)
                .FirstOrDefaultAsync();
            return _cachedLiveId;
        }
        catch (Exception ex) { _log.Warn("live-events", $"LIVE ID resolution failed: {ex.Message}"); return null; }
    }
}
