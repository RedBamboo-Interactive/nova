using System.Text;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// Makes untrusted text safe to place inside a Nova frame (<c>&lt;nova-event&gt;</c>,
/// <c>&lt;nova-context&gt;</c>, ...). Session titles, question text, headers and option labels are
/// written by a worker model; verbatim, a value containing <c>&lt;/nova-event&gt;</c> would close the
/// frame (for the model and for every client parser, which all match the literal close tag) and
/// make the rest read as a new turn from outside the event. Values are also bounded; the full
/// structured payload always stays in the event's metadata.
/// </summary>
internal static class NovaEventText
{
    private static readonly Regex LineBreaks = new(@"\s*[\r\n\p{Zl}\p{Zp}]+\s*", RegexOptions.Compiled);

    // "<", full-width or small-form less-than, optional "/", then "nova-": the start of any opening
    // or closing Nova frame tag, in any case, with whitespace or invisible format characters
    // (zero-width, bidi controls) anywhere inside it.
    private static readonly Regex FrameTag = new(
        $@"[<{(char)0xFF1C}{(char)0xFE64}][\s\p{{Cf}}]*/?[\s\p{{Cf}}]*n\p{{Cf}}*o\p{{Cf}}*v\p{{Cf}}*a[\s\p{{Cf}}]*-",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Appended where a bounded section stops; everything after it is omitted.</summary>
    internal const string SectionMarker =
        " [… truncated to bound this event; every question with all its options is in this event's metadata]";

    /// <summary>Appended where a single value was cut.</summary>
    internal static string TruncationMarker(int omitted)
        => $" [… {omitted} more characters truncated; the full text is in this event's metadata]";

    /// <summary>
    /// Neutralizes any Nova frame tag, folds the value onto one line (so it cannot imitate the
    /// event's own layout), then bounds it. Null and blank stay null.
    /// </summary>
    internal static string? Field(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = LineBreaks.Replace(Neutralize(value), " ").Trim();
        if (safe.Length == 0) return null;
        if (safe.Length <= max) return safe;
        var cut = Cut(safe, max);
        return safe[..cut] + TruncationMarker(safe.Length - cut);
    }

    /// <summary>
    /// A value for a double-quoted frame attribute (an event source, a device name, an agent name):
    /// tags neutralized and the characters that could end the attribute or the tag escaped.
    /// Ordinary values ("heartbeat-tick", "automation:Backup &amp; Sync", "Pixel 9") render exactly as
    /// written.
    /// </summary>
    internal static string AttributeText(string value)
        => Neutralize(value).Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\r", " ").Replace("\n", " ");

    /// <summary>A Field that is also safe inside a double-quoted frame attribute.</summary>
    internal static string Attribute(string? value, int max = 200)
        => (Field(value, max) ?? "").Replace("&", "&amp;").Replace("\"", "&quot;")
            .Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Turns every opening or closing Nova frame tag (any case, spacing, full-width or small-form
    /// bracket, invisible characters inside it) into inert text: only its bracket changes, to
    /// "&amp;lt;". Everything else is left byte for byte, including other angle brackets ("a &lt; b",
    /// code) and invisible characters outside a tag (emoji joiners, bidi marks).
    /// </summary>
    internal static string Neutralize(string value)
        => FrameTag.Replace(value, match => "&lt;" + match.Value[1..]);

    // Never split a surrogate pair.
    private static int Cut(string value, int max)
        => max > 0 && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;

    /// <summary>
    /// Builds a section under a total bound. A part that would cross the bound is cut with the
    /// marker and nothing after it is added.
    /// </summary>
    internal sealed class Bounded(int max)
    {
        private readonly StringBuilder _text = new();

        public Bounded Append(string part)
        {
            if (_text.Length >= max) return this;
            var room = max - _text.Length;
            if (part.Length <= room) _text.Append(part);
            else
            {
                var cut = Cut(part, room);
                _text.Append(part.AsSpan(0, cut)).Append(SectionMarker);
            }
            return this;
        }

        public Bounded AppendLine(string part = "") => Append(part + "\n");

        public override string ToString() => _text.ToString();
    }
}
