using System.Text;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Nova;

internal static partial class DiscussionTitleGeneration
{
    private const int LeadingMessages = 4;
    private const int TrailingMessages = 16;
    private const int MaxMessageCharacters = 600;
    private const int MaxTitleCharacters = 70;

    public static string? BuildPrompt(string? currentTitle, IReadOnlyList<SessionMessage> messages)
    {
        var visible = ConversationExporter.CollapseMessages(messages.ToList())
            .Where(message => message.EventType == "text"
                && message.Role is "user" or "assistant"
                && !(message.Role == "assistant"
                    && string.Equals(message.Phase, "commentary", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(message.Content))
            .ToList();
        if (visible.Count == 0) return null;

        IEnumerable<ConversationExporter.CollapsedMessage> selected = visible.Count <= LeadingMessages + TrailingMessages
            ? visible
            : visible.Take(LeadingMessages).Concat(visible.TakeLast(TrailingMessages));

        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(currentTitle))
            prompt.AppendLine($"Current title: {Normalize(currentTitle, MaxTitleCharacters)}");
        prompt.AppendLine("Conversation:");
        foreach (var message in selected)
        {
            var label = message.Role == "user" ? "User" : "Nova";
            prompt.Append(label).Append(": ")
                .AppendLine(Normalize(message.Content!, MaxMessageCharacters));
        }
        return prompt.ToString().Trim();
    }

    public static string? CleanGeneratedTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var line = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;

        line = TitlePrefix().Replace(line, "").Trim();
        line = line.Trim(' ', '\t', '"', '\'', '`', '*', '_');
        line = line.TrimEnd('.', ',', ':', ';');
        return line.Length is > 0 and <= MaxTitleCharacters ? line : null;
    }

    private static string Normalize(string value, int maxCharacters)
    {
        var normalized = Whitespace().Replace(value, " ").Trim();
        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..(maxCharacters - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^(?:conversation\s+)?title\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
