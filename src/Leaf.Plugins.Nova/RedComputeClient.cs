using System.Net.Http.Json;
using System.Text.Json;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>A single raw message from a RedCompute session transcript.</summary>
public sealed class SessionMessage
{
    public long Id { get; set; }
    public string? SessionId { get; set; }
    public string Role { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? Content { get; set; }
    public string? ToolName { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolResult { get; set; }
    public JsonElement? PayloadRef { get; set; }
    public string? Phase { get; set; }
    public string? MessageId { get; set; }
    public string? Epoch { get; set; }
    public long? Sequence { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTimeOffset? RecordCreatedAt { get; set; }
    public string? AttachmentsJson { get; set; }

    /// <summary>
    /// Provider-neutral message uid. RedCompute mints one per turn, so every
    /// record in an assistant run shares it -- it is the key the chat UI uses as
    /// a block id, and therefore the key reactions are stored against.
    /// </summary>
    public string? MessageUid { get; set; }
}

/// <summary>Point-in-time view of a RedCompute session: status, title, and raw messages.</summary>
public sealed record SessionSnapshot(
    string? Status,
    string? StopReason,
    string? Title,
    List<SessionMessage> Messages);

/// <summary>Session metadata carried by every transcript page.</summary>
public sealed class SessionPageInfo
{
    public string Id { get; set; } = "";
    public string? Status { get; set; }
    public string? StopReason { get; set; }
    public string? Title { get; set; }
}

/// <summary>Authoritative keyset facts returned by RedCompute.</summary>
public sealed class SessionTranscriptPageMetadata
{
    public string Epoch { get; set; } = "";
    public string Direction { get; set; } = "newest";
    public string? OldestCursor { get; set; }
    public string? NewestCursor { get; set; }
    public bool HasEarlier { get; set; }
    public bool HasLater { get; set; }
    public long? FromSequence { get; set; }
    public long? ThroughSequence { get; set; }
    public bool BoundaryComplete { get; set; }
}

/// <summary>One raw, provider-neutral durable transcript page.</summary>
public sealed class SessionTranscriptPage
{
    public SessionPageInfo Session { get; set; } = new();
    public List<SessionMessage> Messages { get; set; } = [];
    public SessionTranscriptPageMetadata Page { get; set; } = new();
}

/// <summary>
/// A transcript-page call keeps RedCompute's status and machine-readable error
/// intact so Nova can proxy cursor and authorization failures without guessing.
/// </summary>
public sealed record SessionTranscriptPageResult(
    int StatusCode,
    SessionTranscriptPage? Value,
    string Content,
    string ContentType)
{
    public bool Success => StatusCode is >= 200 and < 300 && Value is not null;
}

/// <summary>
/// RedCompute (port 18800) session gateway. Local plain-HTTP like the kernel's own
/// AutomationService. Mutating calls carry a signed execution identity.
/// IAiInference is still unwired in the kernel (contract gap); this
/// client needs raw session control (inject, callbacks, interrupt) the SDK interface
/// does not model anyway.
/// </summary>
public sealed class RedComputeClient(IComputeGateway gateway)
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

    public async Task<string?> CreateSessionAsync(Dictionary<string, object?> body,
        ComputeProvenance provenance, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        var resp = await gateway.SendAsync(req, provenance, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return session.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
    }

    public async Task<bool> SetConfidentialAsync(string sessionId,
        ComputeProvenance provenance, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/ai-session/sessions/{sessionId}/confidential")
        {
            Content = JsonContent.Create(new { confidential = true }, options: JsonOptions),
        };
        using var response = await gateway.SendAsync(request, provenance, ct);
        return response.IsSuccessStatusCode;
    }

    public sealed record ExecuteResult(
        bool Success, string? Text, string? Error, string? SessionId, Guid? JobId);

    /// <summary>
    /// One-shot blocking execution via /ai-session/execute — used by automation runs.
    /// The call returns when the session completes (or <paramref name="timeoutSeconds"/>
    /// plus a small grace period elapses).
    /// </summary>
    public async Task<ExecuteResult> ExecuteAsync(object body, string jobName, string? userId,
        int timeoutSeconds, ComputeProvenance provenance, CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/execute")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Add("X-Job-Name", jobName);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 120));

        var resp = await gateway.SendAsync(req, provenance, callCts.Token);
        var raw = await resp.Content.ReadAsStringAsync(callCts.Token);
        var jobId = resp.Headers.TryGetValues("X-Job-Id", out var jobValues)
            && Guid.TryParse(jobValues.FirstOrDefault(), out var parsedJobId)
                ? parsedJobId : (Guid?)null;

        if (!resp.IsSuccessStatusCode)
            return new ExecuteResult(false, null,
                $"RedCompute HTTP {(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 300)]}",
                null, jobId);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var sessionId = root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;

        return new ExecuteResult(success, text, error, sessionId, jobId);
    }

    public sealed record SendMessageResult(
        bool Success, JsonElement? Payload, int StatusCode,
        string? ErrorCode = null, string? ErrorMessage = null);

    public sealed record ProxyResult(int StatusCode, string Content, string ContentType);

    /// <summary>Send a user message while preserving RedCompute's status and machine-readable error.</summary>
    public async Task<SendMessageResult> SendMessageDetailedAsync(
        string sessionId, object body, ComputeProvenance provenance,
        CancellationToken ct = default, string? idempotencyKey = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/message")
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                request.Headers.Add("X-Idempotency-Key", idempotencyKey);
            var resp = await gateway.SendAsync(request, provenance, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            JsonElement? payload = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                payload = doc.RootElement.Clone();
            }
            catch { }

            if (resp.IsSuccessStatusCode)
                return new(true, payload, (int)resp.StatusCode);

            var errorCode = payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
            var errorMessage = payload is { ValueKind: JsonValueKind.Object } m
                && m.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : errorCode;
            return new(false, payload, (int)resp.StatusCode, errorCode, errorMessage);
        }
        catch (Exception ex) when (ex.GetType().Name.Equals(
            "ExecutionIdentityValidationException", StringComparison.Ordinal))
        {
            return new(false, null, 403, "execution_identity_rejected", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return new(false, null, 504, "redcompute_timeout", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new(false, null, 502, "redcompute_unavailable", ex.Message);
        }
        catch (Exception ex)
        {
            return new(false, null, 500, "redcompute_request_failed", ex.Message);
        }
    }

    /// <summary>Compatibility wrapper for callers that only need success and the response payload.</summary>
    public async Task<JsonElement?> SendMessageAsync(string sessionId, object body,
        ComputeProvenance provenance, CancellationToken ct = default)
    {
        var result = await SendMessageDetailedAsync(
            sessionId, body, provenance, ct);
        return result.Success ? result.Payload : null;
    }

    /// <summary>
    /// Proxy one discussion-authorized durable queue operation to RedCompute while
    /// preserving its status and machine-readable response verbatim.
    /// </summary>
    public async Task<ProxyResult> ProxyInputQueueAsync(
        string sessionId, HttpMethod method, string suffix = "",
        ComputeProvenance? provenance = null, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(method,
                $"/ai-session/sessions/{sessionId}/input-queue{suffix}");
            using var response = await gateway.SendAsync(request, provenance, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new((int)response.StatusCode, content, contentType);
        }
        catch (Exception ex)
        {
            return new(502,
                JsonSerializer.Serialize(new { error = "redcompute_unavailable", message = ex.Message }, JsonOptions),
                "application/json");
        }
    }

    public async Task<bool> InjectAsync(string sessionId, object body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/inject")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var resp = await gateway.SendAsync(request, provenance: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RegisterCallbackAsync(string sessionId, string url, bool force = false,
        CancellationToken ct = default, ComputeProvenance? provenance = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/callback")
        {
            Content = JsonContent.Create(new { url, force }, options: JsonOptions),
        };
        using var resp = await gateway.SendAsync(request, provenance, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> StopAsync(string sessionId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/stop");
        using var resp = await gateway.SendAsync(request, provenance: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeAsync(string sessionId, ComputeProvenance provenance, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/resume");
        var resp = await gateway.SendAsync(request, provenance, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task DismissAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/ai-session/sessions/{sessionId}/dismiss");
            using var response = await gateway.SendAsync(request, provenance: null, ct);
        }
        catch { }
    }

    /// <summary>Raw GET /ai-session/sessions/{id} as parsed JSON, or null when unreachable/missing.</summary>
    public async Task<JsonDocument?> GetSessionRawAsync(
        string sessionId, CancellationToken ct = default, int? tail = null)
    {
        try
        {
            var url = tail is { } count
                ? $"/ai-session/sessions/{sessionId}?tail={count}"
                : $"/ai-session/sessions/{sessionId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await gateway.SendAsync(request, provenance: null, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    public async Task<Guid?> GetSessionJobIdAsync(string sessionId, CancellationToken ct = default)
    {
        using var session = await GetSessionRawAsync(sessionId, ct);
        if (session is null) return null;
        var root = session.RootElement;
        if (root.TryGetProperty("jobId", out var job)
            && job.ValueKind == JsonValueKind.String
            && Guid.TryParse(job.GetString(), out var jobId))
            return jobId;
        return null;
    }

    /// <summary>Result of <see cref="ProbeSessionAsync"/>: <c>Reachable</c> false means we
    /// could not get an answer; <c>Status</c> null with <c>Reachable</c> true means the
    /// session no longer exists (a definitive "nothing is running").</summary>
    public sealed record SessionProbe(
        bool Reachable,
        string? Status,
        string? StopReason = null,
        string? ProviderSessionId = null);

    /// <summary>
    /// Provider-neutral state needed by the discussion resume path. A persisted
    /// RedCompute shell can exist before its provider has created a resumable
    /// conversation/thread; <c>ProviderSessionId</c> is the contract-level signal
    /// that distinguishes the two.
    /// </summary>
    public sealed record SessionResumeProbe(bool Reachable, bool Exists, string? ProviderSessionId);

    public async Task<SessionResumeProbe> ProbeSessionForResumeAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/ai-session/sessions/{sessionId}");
            using var resp = await gateway.SendAsync(request, provenance: null, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(true, false, null);
            if (!resp.IsSuccessStatusCode)
                return new(false, false, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("session", out var session))
                return new(true, false, null);

            var providerSessionId = session.TryGetProperty("providerSessionId", out var providerId)
                && providerId.ValueKind == JsonValueKind.String
                    ? providerId.GetString()
                    : null;
            return new(true, true, providerSessionId);
        }
        catch
        {
            return new(false, false, null);
        }
    }

    /// <summary>
    /// Status probe that distinguishes "RedCompute unreachable" from "session gone".
    /// The archive finalizer needs the difference: a 404 proves no process is running,
    /// while a transport error means the stop is unverified and must be retried.
    /// </summary>
    public async Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/ai-session/sessions/{sessionId}");
            using var resp = await gateway.SendAsync(request, provenance: null, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new(true, null);
            if (!resp.IsSuccessStatusCode) return new(false, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("session", out var session))
                return new(true, null);

            var status = session.TryGetProperty("status", out var st)
                && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var stopReason = session.TryGetProperty("stopReason", out var reason)
                && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            var providerSessionId = session.TryGetProperty("providerSessionId", out var providerId)
                && providerId.ValueKind == JsonValueKind.String ? providerId.GetString() : null;
            return new(true, status, stopReason, providerSessionId);
        }
        catch
        {
            return new(false, null);
        }
    }

    /// <summary>Status of a single session ("Active", "Idle", ...), or null when unreachable/missing.</summary>
    public async Task<string?> GetSessionStatusAsync(string sessionId, CancellationToken ct = default)
    {
        var state = await GetSessionStateAsync(sessionId, ct);
        return state?.Status;
    }

    public sealed record SessionRuntimeState(string Status, string? StopReason);

    public async Task<SessionRuntimeState?> GetSessionStateAsync(
        string sessionId, CancellationToken ct = default)
    {
        using var doc = await GetSessionRawAsync(sessionId, ct);
        if (doc == null
            || !doc.RootElement.TryGetProperty("session", out var session)
            || !session.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
            return null;
        var stopReason = session.TryGetProperty("stopReason", out var reason)
            && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
        return new(status.GetString()!, stopReason);
    }

    public async Task<SessionSnapshot?> GetSessionAsync(
        string sessionId, CancellationToken ct = default, int? tail = null)
    {
        using var doc = await GetSessionRawAsync(sessionId, ct, tail);
        if (doc == null) return null;

        string? status = null, stopReason = null, title = null;
        if (doc.RootElement.TryGetProperty("session", out var session))
        {
            if (session.TryGetProperty("status", out var st)) status = st.GetString();
            if (session.TryGetProperty("stopReason", out var sr)
                && sr.ValueKind == JsonValueKind.String) stopReason = sr.GetString();
            if (session.TryGetProperty("title", out var ti)) title = ti.GetString();
        }

        var messages = new List<SessionMessage>();
        if (doc.RootElement.TryGetProperty("messages", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                messages.Add(ParseSessionMessage(el));
            }
        }

        return new SessionSnapshot(status, stopReason, title, messages);
    }

    public async Task<SessionTranscriptPageResult> GetTranscriptPageAsync(
        string sessionId,
        int limit,
        string? before = null,
        string? after = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
            throw new ArgumentException("before and after are mutually exclusive");

        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 500)}" };
        if (!string.IsNullOrWhiteSpace(before))
            query.Add($"before={Uri.EscapeDataString(before)}");
        if (!string.IsNullOrWhiteSpace(after))
            query.Add($"after={Uri.EscapeDataString(after)}");

        var url = $"/ai-session/sessions/{Uri.EscapeDataString(sessionId)}/transcript-page?{string.Join("&", query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await gateway.SendAsync(request, provenance: null, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

        SessionTranscriptPage? page = null;
        if (response.IsSuccessStatusCode)
        {
            try
            {
                page = JsonSerializer.Deserialize<SessionTranscriptPage>(content, JsonOptions);
                if (page is null
                    || page.Session is null
                    || page.Page is null
                    || page.Messages is null
                    || string.IsNullOrWhiteSpace(page.Page.Epoch)
                    || !string.Equals(page.Session.Id, sessionId, StringComparison.Ordinal)
                    || page.Messages.Any(message => !string.IsNullOrWhiteSpace(message.SessionId)
                        && !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal)))
                    page = null;
            }
            catch (JsonException)
            {
                // Keep the malformed upstream response available to the caller,
                // but never present it as a successful typed page.
            }
        }

        return new SessionTranscriptPageResult((int)response.StatusCode, page, content, contentType);
    }

    internal static SessionMessage ParseSessionMessage(JsonElement el) => new()
    {
        Id = el.TryGetProperty("id", out var id) && id.TryGetInt64(out var parsedId) ? parsedId : 0,
        SessionId = el.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String
            ? sessionId.GetString()
            : null,
        Role = el.GetProperty("role").GetString() ?? "unknown",
        EventType = el.TryGetProperty("eventType", out var et) ? et.GetString() ?? "text" : "text",
        Content = el.TryGetProperty("content", out var c) ? c.GetString() : null,
        ToolName = el.TryGetProperty("toolName", out var toolName) && toolName.ValueKind == JsonValueKind.String
            ? toolName.GetString()
            : null,
        ToolInput = ReadStringOrJson(el, "toolInput"),
        ToolResult = ReadStringOrJson(el, "toolResult"),
        PayloadRef = el.TryGetProperty("payloadRef", out var payloadRef)
            && payloadRef.ValueKind == JsonValueKind.Object
                ? payloadRef.Clone()
                : null,
        Phase = el.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String
            ? phase.GetString()
            : null,
        MessageId = el.TryGetProperty("messageId", out var messageId) && messageId.ValueKind == JsonValueKind.String
            ? messageId.GetString()
            : null,
        Epoch = el.TryGetProperty("epoch", out var epoch) && epoch.ValueKind == JsonValueKind.String
            ? epoch.GetString()
            : null,
        Sequence = el.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out var parsedSequence)
            ? parsedSequence
            : null,
        Timestamp = el.TryGetProperty("timestamp", out var ts) ? ts.GetDateTimeOffset().UtcDateTime : DateTime.MinValue,
        RecordCreatedAt = el.TryGetProperty("recordCreatedAt", out var recordCreatedAt)
            && recordCreatedAt.ValueKind == JsonValueKind.String
            && recordCreatedAt.TryGetDateTimeOffset(out var parsedRecordCreatedAt)
                ? parsedRecordCreatedAt.ToUniversalTime()
                : null,
        MessageUid = el.TryGetProperty("messageUid", out var uid) && uid.ValueKind == JsonValueKind.String
            ? uid.GetString()
            : null,
        AttachmentsJson = el.TryGetProperty("attachmentsJson", out var attachmentsJson)
            && attachmentsJson.ValueKind == JsonValueKind.String
                ? attachmentsJson.GetString()
                : null,
    };

    private static string? ReadStringOrJson(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    public sealed record SessionListEntry(
        string Id,
        string Status,
        int MessageCount,
        string? StopReason = null);

    public async Task<List<SessionListEntry>?> GetSessionsAsync(int? limit = null, CancellationToken ct = default)
    {
        try
        {
            var url = limit is { } l ? $"/ai-session/sessions?limit={l}" : "/ai-session/sessions";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await gateway.SendAsync(request, provenance: null, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<SessionListEntry>>(JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }
}
