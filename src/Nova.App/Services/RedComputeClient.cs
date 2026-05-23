using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nova.App.Configuration;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class RedComputeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LogService _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RedComputeClient(NovaConfig config, LogService log)
    {
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:18800"),
            Timeout = TimeSpan.FromSeconds(1800)
        };
        _http.DefaultRequestHeaders.Add("X-Caller-Info", "Nova");
    }

    public async Task<ClaudeResponse> InvokeClaudeAsync(ClaudeRequest request, CancellationToken ct = default)
    {
        _log.Info("redcompute", $"Invoking Claude: {request.SystemPromptHint ?? "chat"}");

        var response = await _http.PostAsJsonAsync("/claude-code/generate", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOptions, ct);
        return result ?? throw new InvalidOperationException("Empty response from RedCompute");
    }

    public async Task<Stream> InvokeClaudeStreamAsync(ClaudeRequest request, CancellationToken ct = default)
    {
        _log.Info("redcompute", $"Invoking Claude (stream): {request.SystemPromptHint ?? "chat"}");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/claude-code/generate")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        httpRequest.Headers.Accept.Add(new("text/event-stream"));

        var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(audio);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "audio", fileName);

        var response = await _http.PostAsync("/stt/transcribe", content, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SttResponse>(JsonOptions, ct);
        return result?.Text ?? "";
    }

    public async Task<byte[]> SpeakAsync(string text, string? voice, string? instructions, CancellationToken ct = default)
    {
        var body = new { text, voice = voice ?? "Serena", instructions };
        var response = await _http.PostAsJsonAsync("/tts/generate", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            request.Model,
            request.System,
            request.Messages,
            request.MaxTokens,
            mode = "oneshot",
            rationale = "Voice prompt",
        };
        var response = await _http.PostAsJsonAsync("/ai-session/generate", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PromptResponse>(JsonOptions, ct);
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
    public Dictionary<string, string>? Context { get; set; }
}

public class ClaudeResponse
{
    public string? Text { get; set; }
    public string? SessionId { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? Error { get; set; }
}

public class ToolCall
{
    public string Name { get; set; } = "";
    public string? Input { get; set; }
    public string? Output { get; set; }
}

public class SttResponse
{
    public string Text { get; set; } = "";
    public string? Language { get; set; }
}

public class PromptRequest
{
    public string? Model { get; set; }
    public string System { get; set; } = "";
    public List<PromptMessage> Messages { get; set; } = [];
    public int MaxTokens { get; set; }
}

public class PromptMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class PromptResponse
{
    public string Text { get; set; } = "";
}
