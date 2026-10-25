using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Leaf.Sdk;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>A branch resolved to the folder a session or discussion works in.</summary>
/// <param name="Path">The worktree folder, or the repository checkout itself when <paramref name="IsCheckout"/>.</param>
public sealed record ResolvedWorktree(
    string RepositoryId, string RepositoryName, string Branch, string Path, bool IsCheckout, bool Created);

public sealed record WorktreeFailure(int StatusCode, string Error, string Message);

public sealed record WorktreeOutcome(ResolvedWorktree? Worktree, WorktreeFailure? Failure)
{
    public bool Ok => Worktree is not null;
    public static WorktreeOutcome Fail(int statusCode, string error, string message)
        => new(null, new WorktreeFailure(statusCode, error, message));
}

/// <summary>
/// Branches and worktrees through Code's API. Code owns them (git is the source of truth); Nova
/// asks Code to find or create the worktree for a branch and never runs git itself.
/// </summary>
/// <remarks>
/// The call goes to the kernel on loopback and carries the caller's own signed credentials (the
/// bearer, else the session cookie) copied from the request being served, so Code authorises the
/// same actor and provenance. A request with no credentials is refused rather than sent unsigned,
/// and the credentials only ever go to the fixed loopback origin.
/// </remarks>
public sealed class CodeWorktreeClient
{
    public const string BaseUrl = "http://127.0.0.1:18804";
    private const string RepositoriesRoute = "/api/apps/codered/repositories";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    // A mutation (git worktree add) can take Code up to two minutes; the timeout covers it.
    public CodeWorktreeClient() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(150) }) { }

    internal CodeWorktreeClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(BaseUrl);
    }

    /// <summary>
    /// The folder for <paramref name="branch"/>: the existing worktree, else a new one, else the
    /// repository checkout when that is where the branch is checked out.
    /// </summary>
    public async Task<WorktreeOutcome> ResolveAsync(
        HttpContext caller, LeafEntity repository, string branch, string? baseRef, CancellationToken ct)
    {
        var name = branch.Trim();
        var route = $"{RepositoriesRoute}/{repository.Id:D}/worktrees";
        try
        {
            var listed = await SendAsync(caller, HttpMethod.Get, route, null, ct);
            if (listed.Failure is not null) return new(null, listed.Failure);
            var list = listed.Body;

            if (Str(list, "branchWork") == "main-only")
                return await MainOnlyAsync(caller, route, repository, name, baseRef, ct);

            if (Find(list, name) is { } existing)
                return Existing(repository, name, existing, created: false);

            var created = await SendAsync(caller, HttpMethod.Post, route,
                new { branch = name, @base = string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim() }, ct);
            // Checked out somewhere the listing did not show (a race): Code names the holder.
            if (created.Failure is { Error: "branch_checked_out" } && created.Body.ValueKind == JsonValueKind.Object
                && created.Body.TryGetProperty("entry", out var holder) && holder.ValueKind == JsonValueKind.Object)
                return Existing(repository, name, holder, created: false);
            if (created.Failure is not null) return new(null, created.Failure);
            if (created.Body.TryGetProperty("entry", out var made) && made.ValueKind == JsonValueKind.Object
                && Str(made, "path") is { Length: > 0 })
                return Existing(repository, name, made, created: true);
            return WorktreeOutcome.Fail(StatusCodes.Status502BadGateway, "code_bad_response",
                "Code created the worktree but did not report where.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return WorktreeOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "code_unavailable",
                $"Code is not reachable, so branches and worktrees cannot be resolved: {ex.Message}");
        }
    }

    /// <summary>Code's own refusal text; it creates nothing for a main-only repository.</summary>
    private async Task<WorktreeOutcome> MainOnlyAsync(
        HttpContext caller, string route, LeafEntity repository, string branch, string? baseRef, CancellationToken ct)
    {
        var refused = await SendAsync(caller, HttpMethod.Post, route, new { branch, @base = baseRef }, ct);
        var message = refused.Failure is { Error: "main_only" } failure
            ? failure.Message
            : $"'{repository.Name}' is set to main only: sessions work in its checkout, not in branches or worktrees.";
        return WorktreeOutcome.Fail(StatusCodes.Status409Conflict, "main_only", message);
    }

    private static WorktreeOutcome Existing(LeafEntity repository, string branch, JsonElement entry, bool created)
    {
        var path = Str(entry, "path");
        if (string.IsNullOrWhiteSpace(path))
            return WorktreeOutcome.Fail(StatusCodes.Status502BadGateway, "code_bad_response", "Code reported a worktree without a path.");
        if (entry.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.True)
            return WorktreeOutcome.Fail(StatusCodes.Status409Conflict, "worktree_missing",
                $"The worktree for '{branch}' is recorded by git but its folder is gone ({path}). Clean up missing worktrees in Code first.");
        var isCheckout = entry.TryGetProperty("isCheckout", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new(new ResolvedWorktree(repository.Id.ToString(), repository.Name, branch,
            Path.GetFullPath(path), isCheckout, created), null);
    }

    private static JsonElement? Find(JsonElement list, string branch)
    {
        if (!list.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && Str(item, "branch") == branch)
                return item;
        return null;
    }

    private sealed record Reply(JsonElement Body, WorktreeFailure? Failure);

    private async Task<Reply> SendAsync(HttpContext caller, HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, route);
        var authorization = caller.Request.Headers.Authorization.ToString();
        var cookie = caller.Request.Headers.Cookie.ToString();
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        else if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        else
            return new(default, new WorktreeFailure(StatusCodes.Status401Unauthorized, "authentication_required",
                "The request carries no signed identity to present to Code."));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        JsonElement parsed = default;
        try { parsed = JsonDocument.Parse(raw).RootElement.Clone(); } catch (JsonException) { }
        var isJson = parsed.ValueKind == JsonValueKind.Object;

        if (response.IsSuccessStatusCode && isJson) return new(parsed, null);
        if (response.IsSuccessStatusCode)
            return new(default, new WorktreeFailure(StatusCodes.Status502BadGateway, "code_bad_response", "Code returned an unreadable answer."));

        var error = isJson ? Str(parsed, "error") : null;
        // A route the kernel does not know is not Code's answer: Code is not installed or not loaded.
        if (response.StatusCode == HttpStatusCode.NotFound && error is null)
            return new(default, new WorktreeFailure(StatusCodes.Status503ServiceUnavailable, "code_unavailable",
                "Code is not available, so branches and worktrees cannot be resolved."));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(default, new WorktreeFailure(StatusCodes.Status403Forbidden, "code_forbidden",
                "Code did not accept the caller's identity for this repository."));
        if ((int)response.StatusCode >= 500 && error is null)
            return new(default, new WorktreeFailure(StatusCodes.Status503ServiceUnavailable, "code_unavailable",
                $"Code is not available (HTTP {(int)response.StatusCode})."));

        var message = (isJson ? Str(parsed, "message") : null) ?? error ?? $"Code refused the request (HTTP {(int)response.StatusCode}).";
        var status = (int)response.StatusCode is >= 400 and < 500 ? (int)response.StatusCode : StatusCodes.Status502BadGateway;
        return new(isJson ? parsed : default, new WorktreeFailure(status, error ?? "code_error", message));
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
