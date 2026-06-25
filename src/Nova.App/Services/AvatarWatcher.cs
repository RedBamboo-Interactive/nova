using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nova.App.Configuration;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;
using RedBamboo.AppHost.WebSockets;

namespace Nova.App.Services;

/// <summary>
/// Connects to RedLeaf's WebSocket and listens for entity.updated events on
/// agent-type entities. When avatar_override changes on the Nova agent, it
/// force-refreshes AgentResolver and broadcasts agent.avatar-changed to all
/// connected Nova frontend clients.
/// </summary>
public sealed class AvatarWatcher
{
    private readonly NovaConfig _config;
    private readonly AgentResolver _agentResolver;
    private readonly WebSocketBroadcaster _broadcaster;
    private readonly LogService _log;
    private string? _lastAvatarValue;
    private Task? _loop;
    private CancellationTokenSource? _cts;

    public AvatarWatcher(NovaConfig config, AgentResolver agentResolver, WebSocketBroadcaster broadcaster, LogService log)
    {
        _config = config;
        _agentResolver = agentResolver;
        _broadcaster = broadcaster;
        _log = log;
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _lastAvatarValue = await FetchAvatarOverrideAsync();
        _log.Info("avatar-watcher", $"Watching agent entities via RedLeaf WS (initial: {_lastAvatarValue ?? "(none)"})");

        var backoff = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await ConnectAndListenAsync(ct, onConnected: () =>
                {
                    connected = true;
                    backoff = TimeSpan.FromSeconds(2);
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!connected)
                    _log.Warn("avatar-watcher", $"WS connect failed: {ex.Message} — retrying in {backoff.TotalSeconds}s");
                else
                    _log.Warn("avatar-watcher", $"WS disconnected: {ex.Message} — reconnecting in {backoff.TotalSeconds}s");
            }

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }

            if (!connected)
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct, Action onConnected)
    {
        using var ws = new ClientWebSocket();
        var token = BuildJwt();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        var wsUri = new Uri(_config.Suite.RedLeaf
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/') + "/ws");

        await ws.ConnectAsync(wsUri, ct);
        onConnected();

        var buffer = new byte[16384];
        using var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            await HandleMessageAsync(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), ct);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "entity.updated")
                return;

            if (!root.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("typeSlug", out var tsEl) || tsEl.GetString() != "agent") return;

            var current = await FetchAvatarOverrideAsync();
            if (current == _lastAvatarValue) return;

            _log.Info("avatar-watcher", $"avatar_override changed: {_lastAvatarValue ?? "(none)"} → {current ?? "(none)"}");
            _lastAvatarValue = current;

            var agents = await _agentResolver.GetAgentsAsync(forceRefresh: true);
            var nova = agents.FirstOrDefault(a => a.Slug == "nova");
            var url = nova != null ? _agentResolver.BuildAvatarUrl(nova.AvatarFilename) : null;

            if (url != null) NovaMirror.AvatarUrl = url;

            _broadcaster.Broadcast("agent.avatar-changed", new
            {
                agentId = NovaMirror.AgentId,
                url = url ?? "",
            });
        }
        catch (Exception ex)
        {
            _log.Warn("avatar-watcher", $"Handle message failed: {ex.Message}");
        }
    }

    private async Task<string?> FetchAvatarOverrideAsync()
    {
        try
        {
            using var http = BuildHttpClient();
            var resp = await http.GetAsync("api/entities/nova");
            if (!resp.IsSuccessStatusCode) return _lastAvatarValue;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl)) return null;

            JsonElement data;
            if (dataEl.ValueKind == JsonValueKind.Object)
                data = dataEl;
            else if (dataEl.ValueKind == JsonValueKind.String)
            {
                using var parsed = JsonDocument.Parse(dataEl.GetString()!);
                data = parsed.RootElement.Clone();
            }
            else return null;

            foreach (var key in new[] { "avatar_override", "avatar-override" })
            {
                if (data.TryGetProperty(key, out var ov) && ov.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(ov.GetString()))
                    return ov.GetString();
            }
            return null;
        }
        catch
        {
            return _lastAvatarValue;
        }
    }

    private string BuildJwt()
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        return jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
    }

    private HttpClient BuildHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(_config.Suite.RedLeaf.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {BuildJwt()}");
        return http;
    }
}
