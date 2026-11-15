using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Nova;

/// <summary>
/// The one LIVE line a provider-account failure gets: a usage limit or a failed sign-in stops every
/// session on that model or provider, not one turn, so the owner hears about it once per episode,
/// never once per session, and never only through a quiet thread.
/// </summary>
/// <remarks>
/// 2026-11-15: the Fable allowance ran out overnight ("You've hit your Fable limit · resets Nov 20,
/// 9pm (Europe/Zurich). ..."). The Nature orchestrator and its worker died on it and the thread just
/// looked quiet for hours. Such errors are never transient, so nothing retries them; this only says so.
/// RedCompute classifies the error (lastTurn.errorKind, callback turnErrorKind); Nova only decides
/// what LIVE reads and when it is the same episode.
/// </remarks>
public static partial class ProviderLimitNotice
{
    public const string EventSource = "provider-limit";

    /// <summary>The error kinds that stop every session on the account, not just this turn.</summary>
    public static bool IsAccountLevel(string? errorKind, string? stopReason)
        => errorKind is "usage_limit" or "authentication"
            // An older RedCompute has no errorKind; its Claude stop reason still says it.
            || errorKind is null && stopReason is "usage_limit" or "authentication_failed";

    /// <summary>The kind, from the error kind or, failing that, the stop reason.</summary>
    public static string Kind(string? errorKind, string? stopReason)
        => errorKind is "authentication" || errorKind is null && stopReason == "authentication_failed"
            ? "authentication" : "usage_limit";

    /// <summary>
    /// The provider's own statement of the failure: its first sentence, which for a limit carries the
    /// reset time ("You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich)."). The advice
    /// and links that follow are dropped.
    /// </summary>
    public static string Statement(string? error)
    {
        var flat = Whitespace().Replace(error ?? "", " ").Trim();
        if (flat.Length == 0) return "no message from the provider";
        var first = FirstSentence().Match(flat);
        var statement = first.Success && first.Length >= 16 ? first.Value : flat;
        return statement.Length <= 240 ? statement : statement[..237] + "...";
    }

    /// <summary>
    /// One episode: the same statement on the same Zurich day. Every session that dies on it shares
    /// the key, so LIVE reads it once. A limit with a daily reset reads the same words every day, so
    /// the day separates its episodes; a limit lasting several days is said again once a day at most.
    /// </summary>
    public static string EpisodeKey(string? error, DateTimeOffset failedAt)
    {
        var normalized = Statement(error).ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"{EventSource}:{TransientTurnRecoveryPolicy.Zurich(failedAt):yyyy-MM-dd}:{hash}";
    }

    /// <summary>The plain line LIVE reads. A confidential thread is not named.</summary>
    public static string Line(string kind, string? error, DateTimeOffset failedAt, string? threadTitle)
    {
        var at = $"{TransientTurnRecoveryPolicy.Zurich(failedAt):HH:mm} Zurich time";
        var where = string.IsNullOrWhiteSpace(threadTitle) ? "" : $" (\"{Clip(threadTitle, 80)}\" stopped on it)";
        return kind == "authentication"
            ? $"Provider sign-in failed at {at}{where}: {Statement(error)} Sessions on that provider fail until it is signed in again."
            : $"Provider usage limit hit at {at}{where}: {Statement(error)} Sessions on that model fail until it resets; nothing retries them.";
    }

    private static string Clip(string value, int max)
    {
        var flat = Whitespace().Replace(value, " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)] + "...";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Up to the first full stop, exclamation or question mark that ends a sentence (followed by a
    // space or the end), never one inside a URL or a number.
    [GeneratedRegex(@"^.*?[.!?](?=\s|$)")]
    private static partial Regex FirstSentence();
}
