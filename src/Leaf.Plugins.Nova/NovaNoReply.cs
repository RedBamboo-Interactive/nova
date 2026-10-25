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
}
