using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Leaf.Sdk.Services;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>One catalog row as the readiness line needs it; Nova's own copy of the kernel's snapshot.</summary>
public sealed record ExtensionReadinessRow(
    string Id, string? Name, bool Installed, string? PluginStatus, string? State, string? NextAction);

/// <summary>Reads every catalog row with its readiness on this PC.</summary>
public delegate Task<IReadOnlyList<ExtensionReadinessRow>> ExtensionReadinessSource(CancellationToken ct);

/// <summary>
/// One short <c>&lt;nova-context&gt;</c> line naming the installed extensions that are not ready, so an
/// Agent knows what is broken without pulling the whole catalog on every turn:
/// <c>Extensions needing attention: Context Graph (needs setup: Turn it on)</c>.
/// </summary>
/// <remarks>
/// The source is the kernel's in-process extension catalog (<see cref="KernelExtensionCatalog"/>), the
/// same rows and readiness as <c>GET /api/extensions/catalog</c>. It is cached for a minute and bounded
/// by a short timeout because it sits on the message-send path (a marketplace miss can reach the
/// network). No catalog, or any failure, yields "unknown", which omits the line; it never reads as "all
/// ready". Installation state is the owner's private information, so an external audience always gets
/// nothing. Extensions the person turned off are not listed: that is a choice, not a fault.
/// </remarks>
public sealed class ExtensionReadinessContext
{
    internal const string Prefix = "Extensions needing attention: ";
    internal const int MaxLineLength = 300;
    private const int MaxPartLength = 60;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(2);

    private readonly ExtensionReadinessSource? _source;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Cached? _cached;

    // "" when every installed extension is ready; null when the catalog could not be read.
    private sealed record Cached(string? Attention, DateTimeOffset ReadAt);

    public ExtensionReadinessContext(ExtensionReadinessSource? source, TimeProvider? time = null, ILogger? logger = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// The attention line for <paramref name="audience"/>: null when it must be omitted (external
    /// audience, no catalog on this kernel, or a failed read), <c>""</c> when every installed,
    /// enabled extension is ready.
    /// </summary>
    public async Task<string?> GetAttentionAsync(ContextAudience audience, CancellationToken ct)
    {
        if (audience != ContextAudience.PrivateLeaf || _source is null) return null;
        if (Fresh() is { } hit) return hit.Attention;

        await _refresh.WaitAsync(ct);
        try
        {
            if (Fresh() is { } raced) return raced.Attention;
            var attention = await ReadAsync(_source, ct);
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

    private async Task<string?> ReadAsync(ExtensionReadinessSource source, CancellationToken ct)
    {
        try
        {
            var rows = await source(ct).WaitAsync(FetchTimeout, _time, ct);
            return Format(rows);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Extension catalog unreadable; readiness line omitted");
            return null;
        }
    }

    /// <summary>
    /// The line for the catalog rows: installed, enabled rows whose readiness was evaluated and is not
    /// <c>ready</c>, in catalog order, capped at <see cref="MaxLineLength"/>. <c>""</c> when none;
    /// null when there are no rows to judge.
    /// </summary>
    internal static string? Format(IReadOnlyList<ExtensionReadinessRow>? rows)
    {
        if (rows is null) return null;

        var entries = new List<string>();
        foreach (var row in rows)
        {
            if (row is null || !row.Installed || row.State is null || row.State == "ready") continue;
            if (string.Equals(row.PluginStatus, "disabled", StringComparison.OrdinalIgnoreCase)) continue;

            var name = Clean(string.IsNullOrWhiteSpace(row.Name) ? row.Id : row.Name);
            if (name.Length == 0) continue;
            var detail = Clean(row.NextAction);
            var label = StateLabel(row.State);
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

/// <summary>
/// The kernel's optional <see cref="IExtensionCatalog"/> (Leaf.Sdk, KernelApi 0.1.6) as an
/// <see cref="ExtensionReadinessSource"/>. Nova runs against the kernel's own Leaf.Sdk, and an older
/// kernel's has no such type: naming it throws while the method that names it is compiled. So only the
/// non-inlined methods below name it, and the binder turns that failure into "no catalog".
/// </summary>
public static class KernelExtensionCatalog
{
    public static ExtensionReadinessSource? TryResolve(IServiceProvider services, ILogger? logger = null)
    {
        try
        {
            return Bind(services);
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMemberException or FileNotFoundException
                                       or FileLoadException or BadImageFormatException)
        {
            logger?.LogInformation("This kernel has no extension catalog service; the readiness line is off ({Error})",
                ex.GetType().Name);
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ExtensionReadinessSource? Bind(IServiceProvider services)
        => services.GetService<IExtensionCatalog>() is { } catalog ? From(catalog) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ExtensionReadinessSource From(IExtensionCatalog catalog)
        => async ct => (await catalog.GetReadinessAsync(ct))
            .Select(row => new ExtensionReadinessRow(row.Id, row.Name, row.Installed, row.PluginStatus, row.State, row.NextAction))
            .ToArray();
}
