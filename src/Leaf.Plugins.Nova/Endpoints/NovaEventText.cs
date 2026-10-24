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
    // Format characters (zero-width, bidi controls) and control characters other than tab,
    // line feed and carriage return: invisible, so they can hide a tag from a reader.
    private static readonly Regex Invisible = new(@"[\p{Cf}\p{Cc}-[\t\n\r]]", RegexOptions.Compiled);

    private static readonly Regex LineBreaks = new(@"\s*[\r\n\p{Zl}\p{Zp}]+\s*", RegexOptions.Compiled);

    // "<", full-width or small-form less-than, optional "/", spacing, then "nova-": the start of
    // any opening or closing Nova frame tag, in any case.
    private static readonly Regex FrameTag = new(
        $@"[<{(char)0xFF1C}{(char)0xFE64}]\s*/?\s*nova\s*-",
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

    /// <summary>A Field that is also safe inside a double-quoted frame attribute.</summary>
    internal static string Attribute(string? value, int max = 200)
        => (Field(value, max) ?? "").Replace("&", "&amp;").Replace("\"", "&quot;")
            .Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Turns every opening or closing Nova frame tag (any case, spacing, full-width or small-form
    /// bracket, invisible characters inside it) into inert text: its bracket becomes "&amp;lt;".
    /// Other angle brackets, as in "a &lt; b" or code, are kept.
    /// </summary>
    internal static string Neutralize(string value)
        => FrameTag.Replace(Invisible.Replace(value, ""), match => "&lt;" + match.Value[1..]);

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
