using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

public sealed record JournalAutomationInfo(string Id, string Name, string? AgentId);

public sealed record JournalRepositoryInfo(string Id, string? Slug, string Name, string? RemoteUrl);

public sealed record JournalCommitInfo(string Hash, string? ShortHash, string? Message);

/// <summary>A source that answered badly. A short read, reported as partial rather than as a short day.</summary>
public sealed class JournalSourceUnavailableException(string sourceName, string message)
    : Exception(message)
{
    public string SourceName { get; } = sourceName;
}

/// <summary>
/// The Journal's optional neighbours. Null means the neighbour isn't installed, so its fields are
/// omitted; a neighbour that is there and failing throws, so the day reads as partial.
/// </summary>
public interface IJournalPeers
{
    Task<IReadOnlyList<JournalAutomationInfo>?> AutomationsAsync(HttpContext caller, CancellationToken ct);
    Task<IReadOnlyList<JournalRepositoryInfo>?> RepositoriesAsync(HttpContext caller, CancellationToken ct);
    Task<IReadOnlyList<JournalCommitInfo>?> CommitsAsync(HttpContext caller, string repository, DateTimeOffset since, CancellationToken ct);
}

/// <summary>
/// The kernel's and Code's routes on loopback, carrying the caller's own signed credentials, the way
/// <see cref="CodeWorktreeClient"/> does: they authorise the same actor, and the credentials only
/// ever go to the fixed loopback origin. Bounded in time so one slow neighbour can't hold a day open.
/// </summary>
public sealed class JournalPeers : IJournalPeers
{
    private const string AutomationsRoute = "/api/automations/status";
    private const string RepositoriesRoute = "/api/apps/codered/repositories";
    private const string CommitsRoute = "/api/apps/codered/git/commits";
    private const int CommitsPerPage = 200;
    private const int CommitPages = 3;

    private readonly HttpClient _http;

    public JournalPeers() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }) { }

    internal JournalPeers(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(CodeWorktreeClient.BaseUrl);
    }

    public async Task<IReadOnlyList<JournalAutomationInfo>?> AutomationsAsync(HttpContext caller, CancellationToken ct)
    {
        using var body = await GetAsync(caller, AutomationsRoute, "automations", ct);
        if (body is null) return null;
        return Items(body.RootElement, "items")
            .Where(item => Str(item, "id") is not null)
            .Select(item => new JournalAutomationInfo(Str(item, "id")!, Str(item, "name") ?? "", Str(item, "agentId")))
            .ToList();
    }

    public async Task<IReadOnlyList<JournalRepositoryInfo>?> RepositoriesAsync(HttpContext caller, CancellationToken ct)
    {
        using var body = await GetAsync(caller, RepositoriesRoute, "code", ct);
        if (body is null) return null;
        return Items(body.RootElement, "items", "repositories")
            .Where(item => Str(item, "id") is not null)
            .Select(item => new JournalRepositoryInfo(
                Str(item, "id")!, Str(item, "slug"), Str(item, "name") ?? Str(item, "slug") ?? Str(item, "id")!,
                Str(item, "remoteUrl")))
            .ToList();
    }

    public async Task<IReadOnlyList<JournalCommitInfo>?> CommitsAsync(
        HttpContext caller, string repository, DateTimeOffset since, CancellationToken ct)
    {
        var result = new List<JournalCommitInfo>();
        for (var page = 1; page <= CommitPages; page++)
        {
            var route = $"{CommitsRoute}?repository={Uri.EscapeDataString(repository)}"
                + $"&since={Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))}"
                + $"&per_page={CommitsPerPage}&page={page}";
            using var body = await GetAsync(caller, route, "code", ct);
            if (body is null) return page == 1 ? null : result;
            result.AddRange(Items(body.RootElement, "commits")
                .Where(item => Str(item, "hash") is not null)
                .Select(item => new JournalCommitInfo(Str(item, "hash")!, Str(item, "short_hash"), Str(item, "message"))));
            if (!body.RootElement.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True) break;
        }
        return result;
    }

    private async Task<JsonDocument?> GetAsync(HttpContext caller, string route, string source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        var authorization = caller.Request.Headers.Authorization.ToString();
        var cookie = caller.Request.Headers.Cookie.ToString();
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        else if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        else
            throw new JournalSourceUnavailableException(source, "The request carries no identity to present.");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            // A route the kernel doesn't know is the neighbour being absent, not the neighbour failing.
            if (response.StatusCode == HttpStatusCode.NotFound && !HasErrorCode(raw)) return null;
            if (!response.IsSuccessStatusCode)
                throw new JournalSourceUnavailableException(source, $"HTTP {(int)response.StatusCode}");
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex) { throw new JournalSourceUnavailableException(source, ex.Message); }
        catch (HttpRequestException ex) { throw new JournalSourceUnavailableException(source, ex.Message); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new JournalSourceUnavailableException(source, ex.Message);
        }
    }

    private static bool HasErrorCode(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ok", out _);
        }
        catch (JsonException) { return false; }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var items)
                && items.ValueKind == JsonValueKind.Array)
                return items.EnumerateArray().ToList();
        return [];
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
