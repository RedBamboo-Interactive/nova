using System.Net.Http.Json;
using System.Text.Json;

namespace Leaf.Plugins.Nova;

/// <summary>A single raw message from a RedCompute session transcript.</summary>
public sealed class SessionMessage
{
    public string Role { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? Content { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>Point-in-time view of a RedCompute session: status, title, and raw messages.</summary>
public sealed record SessionSnapshot(string? Status, string? Title, List<SessionMessage> Messages);

/// <summary>
/// RedCompute (port 18800) session gateway. Local plain-HTTP like the kernel's own
/// AutomationService — RedCompute wants <c>X-User-Id</c>/<c>X-Caller-Info</c> headers,
/// not a bearer. IAiInference is still unwired in the kernel (contract gap); this
/// client needs raw session control (inject, callbacks, interrupt) the SDK interface
/// does not model anyway.
/// </summary>
public sealed class RedComputeClient
{
    public const string BaseUrl = "http://127.0.0.1:18800";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };

    // One-shot /ai-session/execute calls block for the whole session; the per-call
    // linked CancelAfter governs the timeout, not the client (same as the kernel's
    // AutomationService dispatch client).
    private readonly HttpClient _executeHttp = new()
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<string?> CreateSessionAsync(Dictionary<string, object?> body, string? userId, string callerInfo = "Nova:agent", CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Add("X-Caller-Info", callerInfo);
        req.Headers.Add("X-User-Id", string.IsNullOrWhiteSpace(userId) ? "local-user" : userId);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return session.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
    }

    public sealed record ExecuteResult(bool Success, string? Text, string? Error, string? SessionId);

    /// <summary>
    /// One-shot blocking execution via /ai-session/execute — used by automation runs.
    /// The call returns when the session completes (or <paramref name="timeoutSeconds"/>
    /// plus a small grace period elapses).
    /// </summary>
    public async Task<ExecuteResult> ExecuteAsync(object body, string jobName, string? userId, int timeoutSeconds, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/execute")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Add("X-Job-Name", jobName);
        req.Headers.Add("X-Caller-Info", "Nova:automation");
        req.Headers.Add("X-User-Id", string.IsNullOrWhiteSpace(userId) ? "local-user" : userId);

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 120));

        var resp = await _executeHttp.SendAsync(req, callCts.Token);
        var raw = await resp.Content.ReadAsStringAsync(callCts.Token);

        if (!resp.IsSuccessStatusCode)
            return new ExecuteResult(false, null, $"RedCompute HTTP {(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}", null);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var sessionId = root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;

        return new ExecuteResult(success, text, error, sessionId);
    }

    /// <summary>Send a user message. Returns the raw response element (carries <c>messageUid</c>, <c>sent</c>).</summary>
    public async Task<JsonElement?> SendMessageAsync(string sessionId, object body, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/ai-session/sessions/{sessionId}/message", body, JsonOptions, ct);
        if (!resp.IsSuccessStatusCode) return null;
        try { return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct); }
        catch { return default(JsonElement); }
    }

    public async Task<bool> InjectAsync(string sessionId, object body, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/ai-session/sessions/{sessionId}/inject", body, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RegisterCallbackAsync(string sessionId, string url, bool force = false, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/ai-session/sessions/{sessionId}/callback", new { url, force }, JsonOptions, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> StopAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/ai-session/sessions/{sessionId}/stop", null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/ai-session/sessions/{sessionId}/resume", null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task DismissAsync(string sessionId, CancellationToken ct = default)
    {
        try { await _http.PostAsync($"/ai-session/sessions/{sessionId}/dismiss", null, ct); }
        catch { }
    }

    /// <summary>Raw GET /ai-session/sessions/{id} as parsed JSON, or null when unreachable/missing.</summary>
    public async Task<JsonDocument?> GetSessionRawAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"/ai-session/sessions/{sessionId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Result of <see cref="ProbeSessionAsync"/>: <c>Reachable</c> false means we
    /// could not get an answer; <c>Status</c> null with <c>Reachable</c> true means the
    /// session no longer exists (a definitive "nothing is running").</summary>
    public sealed record SessionProbe(bool Reachable, string? Status);

    /// <summary>
    /// Status probe that distinguishes "RedCompute unreachable" from "session gone".
    /// The archive finalizer needs the difference: a 404 proves no process is running,
    /// while a transport error means the stop is unverified and must be retried.
    /// </summary>
    public async Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"/ai-session/sessions/{sessionId}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new(true, null);
            if (!resp.IsSuccessStatusCode) return new(false, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var status = doc.RootElement.TryGetProperty("session", out var session)
                && session.TryGetProperty("status", out var st) ? st.GetString() : null;
            return new(true, status);
        }
        catch
        {
            return new(false, null);
        }
    }

    /// <summary>Status of a single session ("Active", "Idle", ...), or null when unreachable/missing.</summary>
    public async Task<string?> GetSessionStatusAsync(string sessionId, CancellationToken ct = default)
    {
        using var doc = await GetSessionRawAsync(sessionId, ct);
        if (doc == null) return null;
        return doc.RootElement.TryGetProperty("session", out var session)
            && session.TryGetProperty("status", out var st) ? st.GetString() : null;
    }

    public async Task<SessionSnapshot?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var doc = await GetSessionRawAsync(sessionId, ct);
        if (doc == null) return null;

        string? status = null, title = null;
        if (doc.RootElement.TryGetProperty("session", out var session))
        {
            if (session.TryGetProperty("status", out var st)) status = st.GetString();
            if (session.TryGetProperty("title", out var ti)) title = ti.GetString();
        }

        var messages = new List<SessionMessage>();
        if (doc.RootElement.TryGetProperty("messages", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                messages.Add(new SessionMessage
                {
                    Role = el.GetProperty("role").GetString() ?? "unknown",
                    EventType = el.TryGetProperty("eventType", out var et) ? et.GetString() ?? "text" : "text",
                    Content = el.TryGetProperty("content", out var c) ? c.GetString() : null,
                    Timestamp = el.TryGetProperty("timestamp", out var ts) ? ts.GetDateTime() : DateTime.MinValue,
                });
            }
        }

        return new SessionSnapshot(status, title, messages);
    }

    public sealed record SessionListEntry(string Id, string Status, int MessageCount);

    public async Task<List<SessionListEntry>?> GetSessionsAsync(int? limit = null, CancellationToken ct = default)
    {
        try
        {
            var url = limit is { } l ? $"/ai-session/sessions?limit={l}" : "/ai-session/sessions";
            return await _http.GetFromJsonAsync<List<SessionListEntry>>(url, JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }
}
