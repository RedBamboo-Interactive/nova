namespace Leaf.Plugins.Nova;

/// <summary>
/// The Nova-chat silent settle, mirroring Discord's <c>&lt;discord-no-reply/&gt;</c>: an assistant
/// final that is exactly this marker ends the turn with no visible message. RedCompute keeps the
/// record in the canonical transcript (its sequence must not leave a gap); Nova's projections
/// (chat view, unread revision, export, voice, titles, reaction previews, /ask) treat it as silence.
/// </summary>
public static class NovaNoReply
{
    public const string Marker = "<nova-no-reply/>";

    /// <summary>The whole text is the marker, ignoring surrounding whitespace.</summary>
    public static bool IsMarker(string? text)
        => text is not null && string.Equals(text.Trim(), Marker, StringComparison.Ordinal);

    /// <summary>
    /// What a person reads or hears of a reply with the marker alone on its last line: the words,
    /// without the marker (empty when the marker was all of it). Prose about the marker is unchanged.
    /// The record keeps it; hiding real words would be the worse mistake.
    /// </summary>
    public static string WithoutTrailingMarker(string text)
    {
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith(Marker, StringComparison.Ordinal)) return text;
        var before = trimmed[..^Marker.Length].TrimEnd(' ', '\t');
        return before.Length == 0 || before[^1] == '\n' ? before.TrimEnd() : text;
    }
}
