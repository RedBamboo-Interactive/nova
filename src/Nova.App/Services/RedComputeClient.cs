using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nova.App.Configuration;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class RedComputeClient : IDisposable
{
    private HttpClient _http;
    private readonly LogService _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseUrl;

    public RedComputeClient(NovaConfig config, LogService log)
    {
        _log = log;
        _baseUrl = config.Suite.RedCompute;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(7200)
        };
        _http.DefaultRequestHeaders.Add("X-Caller-Info", "Nova");
    }

    public void SetAuthFactory(AuthenticatedHttpClientFactory factory)
    {
        var newClient = factory.CreateClient(_baseUrl, TimeSpan.FromSeconds(7200));
        newClient.DefaultRequestHeaders.Add("X-Caller-Info", "Nova");
        _http = newClient;
    }

    public async Task<ClaudeResponse> InvokeAsync(ClaudeRequest request, string? userId = null, CancellationToken ct = default, ResolvedMode? mode = null)
    {
        var jobName = request.SystemPromptHint ?? "chat";
        var provider = mode?.Provider ?? "claude-code";
        _log.Info("redcompute", $"Invoking {provider}: {jobName}{(mode != null ? $" [{mode.Model}]" : "")}");

        // Explicit request values win over the mode's defaults — automations (e.g. dreaming) set
        // their own long timeout that must not be clobbered by a tier's shorter one. Null model/
        // effort are dropped by JsonOptions, preserving the original provider-only request shape.
        var body = new
        {
            prompt = request.SystemPrompt != null
                ? $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}"
                : request.Prompt,
            provider,
            workingDir = request.WorkingDirectory,
            model = mode?.Model,
            effort = mode?.Effort,
            allowedTools = request.AllowedTools,
            maxTurns = request.MaxTurns ?? mode?.MaxTurns,
            timeout = request.Timeout ?? mode?.Timeout,
        };

        var msg = new HttpRequestMessage(HttpMethod.Post, "/ai-session/execute")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        msg.Headers.Add("X-Job-Name", $"Nova: {jobName}");
        if (userId != null)
            msg.Headers.Add("X-User-Id", userId);

        var response = await _http.SendAsync(msg, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("Empty response from RedCompute");
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

public class ClaudeRequest
{
    public string Prompt { get; set; } = "";
    public string? SystemPrompt { get; set; }
    public string? SystemPromptHint { get; set; }
    public string? WorkingDirectory { get; set; }
    public List<string>? AllowedTools { get; set; }
    public int? MaxTurns { get; set; }
    public int? Timeout { get; set; }
    public Dictionary<string, string>? Context { get; set; }
}

public class ClaudeResponse
{
    public bool Success { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
    public string? SessionId { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
}

