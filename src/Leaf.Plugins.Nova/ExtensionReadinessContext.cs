using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>Who reads the context a line is built for.</summary>
public enum ContextAudience
{
    /// <summary>A private Leaf discussion with the owner.</summary>
    PrivateLeaf,
    /// <summary>Discord, Leaf Commons, or any other audience outside the owner's private surface.</summary>
    External,
}

/// <summary>
/// One short <c>&lt;nova-context&gt;</c> line naming the installed extensions that are not ready, so an
/// Agent knows what is broken without pulling the whole catalog on every turn:
/// <c>Extensions needing attention: Context Graph (needs setup: Turn it on)</c>.
/// </summary>
/// <remarks>
/// The source is the kernel catalog's readiness summary (<c>GET /api/extensions/catalog</c>). It is read
/// on loopback like <see cref="LivePoller"/>'s other kernel reads, cached for a minute, and bounded by a
/// short timeout because it sits on the message-send path. Any failure yields "unknown", which omits the
/// line; it never reads as "all ready". Installation state is the owner's private information, so an
/// external audience always gets nothing.
/// </remarks>
public sealed class ExtensionReadinessContext
{
    public const string KernelBase = "http://127.0.0.1:18804";
    internal const string CatalogRoute = "/api/extensions/catalog?lang=en";
    internal const string Prefix = "Extensions needing attention: ";
    internal const int MaxLineLength = 300;
    private const int MaxPartLength = 60;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Cached? _cached;

    // "" when every installed extension is ready; null when the catalog could not be read.
    private sealed record Cached(string? Attention, DateTimeOffset ReadAt);

    public ExtensionReadinessContext(ILogger<ExtensionReadinessContext> logger)
        : this(new HttpClient { BaseAddress = new Uri(KernelBase), Timeout = FetchTimeout }, TimeProvider.System, logger) { }

    internal ExtensionReadinessContext(HttpClient http, TimeProvider time, ILogger? logger = null)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(KernelBase);
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The attention line for <paramref name="audience"/>: null when it must be omitted (external
    /// audience, or the catalog could not be read), <c>""</c> when every installed extension is ready.
    /// </summary>
    public async Task<string?> GetAttentionAsync(ContextAudience audience, CancellationToken ct)
    {
        if (audience != ContextAudience.PrivateLeaf) return null;
        if (Fresh() is { } hit) return hit.Attention;

        await _refresh.WaitAsync(ct);
        try
        {
            if (Fresh() is { } raced) return raced.Attention;
            var attention = await ReadAsync(ct);
            _cached = new Cached(attention, _time.GetUtcNow());
            return attention;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private Cached? Fresh()
        => _cached is { } cached && _time.GetUtcNow() - cached.ReadAt < CacheLifetime ? cached : null;

    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using var response = await _http.GetAsync(CatalogRoute, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("Extension catalog returned {Status}; readiness line omitted", (int)response.StatusCode);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return Format(document.RootElement);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Extension catalog unreadable; readiness line omitted");
            return null;
        }
    }

    /// <summary>
    /// The line for a catalog response: installed rows whose readiness was evaluated and is not
    /// <c>ready</c>, in catalog order, capped at <see cref="MaxLineLength"/>. <c>""</c> when none;
    /// null when the response is not a catalog.
    /// </summary>
    internal static string? Format(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object
            || !catalog.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        var entries = new List<string>();
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.True
                || !row.TryGetProperty("readiness", out var readiness) || readiness.ValueKind != JsonValueKind.Object)
                continue;
            var state = Text(readiness, "state");
            if (state is null || state == "ready") continue;

            var name = Clean(Text(row, "name") ?? Text(row, "id"));
            if (name.Length == 0) continue;
            var detail = readiness.TryGetProperty("nextAction", out var next) && next.ValueKind == JsonValueKind.Object
                ? Clean(Text(next, "label"))
                : "";
            var label = StateLabel(state);
            entries.Add(detail.Length > 0 ? $"{name} ({label}: {detail})" : $"{name} ({label})");
        }

        return Cap(entries);
    }

    // Every entry that fits, then "and N more" for the rest; never longer than MaxLineLength.
    private static string Cap(IReadOnlyList<string> entries)
    {
        if (entries.Count == 0) return "";
        for (var shown = entries.Count; shown > 0; shown--)
        {
            var line = Prefix + string.Join(", ", entries.Take(shown))
                + (shown < entries.Count ? $" and {entries.Count - shown} more" : "");
            if (line.Length <= MaxLineLength) return line;
        }
        return Prefix + entries.Count.ToString(CultureInfo.InvariantCulture)
            + (entries.Count == 1 ? " extension" : " extensions");
    }

    private static string StateLabel(string state) => state switch
    {
        "needs-setup" => "needs setup",
        "degraded" => "needs attention",
        "unsupported" => "not supported on this PC",
        _ => Clean(state.Replace('-', ' ')),
    };

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Names and labels come from package guides and manifests: keep each on one line, drop invisible
    // formatting characters (bidi overrides included), and bound its length.
    internal static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.Control
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        if (sb.Length <= MaxPartLength) return sb.ToString();
        var cut = MaxPartLength - 1;
        if (char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut) + "…";
    }
}
