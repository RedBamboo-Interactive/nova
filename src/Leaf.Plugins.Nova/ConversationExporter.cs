using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Markdown export of discussions: the live RedCompute transcript when the session
/// still exists, the persisted stream records otherwise.
/// </summary>
public sealed class ConversationExporter(RedComputeClient redCompute, IDiscussions discussions)
{
    internal static readonly Regex NovaContextTag = new(
        @"<nova-context[^>]*>[\s\S]*?</nova-context>\s*", RegexOptions.Compiled);

    internal static readonly Regex NovaPriorTag = new(
        @"<nova-prior-message[s]?[^>]*>[\s\S]*?</nova-prior-message[s]?>\s*", RegexOptions.Compiled);

    public async Task<string> ExportAsync(IReadOnlyList<DiscussionRead> discussionsToExport, DateTime since, CancellationToken ct = default)
    {
        if (discussionsToExport.Count == 0)
            return $"# No conversations since {since:yyyy-MM-dd}\n";

        var sb = new StringBuilder();
        sb.AppendLine($"# Conversations since {since:yyyy-MM-dd}");
        sb.AppendLine($"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {discussionsToExport.Count} discussion(s)");
        sb.AppendLine();

        foreach (var disc in discussionsToExport)
        {
            await AppendDiscussionAsync(sb, disc, ct);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> ExportSingleAsync(DiscussionRead disc, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await AppendDiscussionAsync(sb, disc, ct);
        return sb.ToString();
    }

    private async Task AppendDiscussionAsync(StringBuilder sb, DiscussionRead disc, CancellationToken ct)
    {
        var snapshot = disc.SessionId is not null
            ? await redCompute.GetSessionAsync(disc.SessionId, ct)
            : null;

        var records = await discussions.GetMessagesAsync(disc.EntityId, ct: ct);
        var localEvents = records
            .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
            .ToList();

        if (snapshot is { Messages.Count: > 0 })
            AppendSessionExport(sb, disc, snapshot.Messages, records, localEvents);
        else
            AppendLocalExport(sb, disc, records);
    }

    private static void AppendSessionExport(StringBuilder sb, DiscussionRead disc,
        List<SessionMessage> raw, IReadOnlyList<DiscussionMessage> records,
        List<DiscussionMessage> localEvents)
    {
        var collapsed = CollapseMessages(raw);
        var textMessages = collapsed.Where(m => m.EventType == "text" && !string.IsNullOrWhiteSpace(m.Content)).ToList();
        var userAttachmentsByUid = records
            .Where(m => m.Metadata["source"]?.GetValue<string>() == "user-message")
            .Where(m => !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>()))
            .GroupBy(m => m.Metadata["uid"]!.GetValue<string>())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        var eventEntries = localEvents.Select(e => new CollapsedMessage
        {
            Role = "event",
            EventType = "event",
            Content = e.Content,
            Timestamp = e.CreatedAt.UtcDateTime,
            Source = e.Metadata["source"]?.GetValue<string>(),
        }).ToList();

        var merged = textMessages.Concat(eventEntries).OrderBy(m => m.Timestamp).ToList();

        var countLabel = eventEntries.Count > 0
            ? $"{textMessages.Count} message(s), {eventEntries.Count} event(s)"
            : $"{textMessages.Count} message(s)";

        sb.AppendLine($"## {disc.Title ?? "Untitled"} [{disc.Id}]");
        sb.AppendLine($"Created: {disc.CreatedAt:yyyy-MM-dd HH:mm} — {countLabel}");
        sb.AppendLine();

        foreach (var msg in merged)
        {
            if (msg.EventType == "event")
            {
                var source = msg.Source?.Replace("event:", "") ?? "system";
                sb.AppendLine($"[event:{source}] ({msg.Timestamp:HH:mm}): {msg.Content}");
            }
            else
            {
                var role = msg.Role == "user" ? "user" : "nova";
                sb.AppendLine($"**{role}** ({msg.Timestamp:HH:mm}):");
                sb.AppendLine(msg.Content);
                if (msg.Role == "user" && msg.MessageUid is not null
                    && userAttachmentsByUid.TryGetValue(msg.MessageUid, out var saved))
                    AppendImageParts(sb, saved.Metadata["parts_json"]?.GetValue<string>());
            }
            sb.AppendLine();
        }
    }

    private static void AppendLocalExport(StringBuilder sb, DiscussionRead disc, IReadOnlyList<DiscussionMessage> messages)
    {
        sb.AppendLine($"## {disc.Title ?? "Untitled"} [{disc.Id}]");
        sb.AppendLine($"Created: {disc.CreatedAt:yyyy-MM-dd HH:mm} — {messages.Count} message(s) [persisted records only, session expired]");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var role = msg.Role == "user" ? "user" : "nova";
            sb.AppendLine($"**{role}** ({msg.CreatedAt:HH:mm}):");

            var partsJson = msg.Metadata["parts_json"]?.GetValue<string>();
            if (msg.Metadata["source"]?.GetValue<string>() == "user-message")
            {
                if (!string.IsNullOrWhiteSpace(msg.Content))
                    sb.AppendLine(msg.Content);
                AppendImageParts(sb, partsJson);
            }
            else if (!string.IsNullOrEmpty(partsJson))
                AppendParts(sb, partsJson);
            else
                sb.AppendLine(msg.Content);

            sb.AppendLine();
        }
    }

    internal static List<CollapsedMessage> CollapseMessages(List<SessionMessage> raw)
    {
        var result = new List<CollapsedMessage>();

        foreach (var msg in raw)
        {
            if (msg.EventType is "thinking" or "status") continue;

            var last = result.Count > 0 ? result[^1] : null;

            if (msg.EventType == "text" && last is { EventType: "text" } && last.Role == msg.Role)
            {
                last.Content = (last.Content ?? "") + (msg.Content ?? "");
                continue;
            }

            var content = msg.Content;
            if (msg.EventType == "text" && msg.Role == "user")
                content = StripInjectedTags(content ?? "");

            result.Add(new CollapsedMessage
            {
                Role = msg.Role,
                EventType = msg.EventType,
                Content = content,
                Timestamp = msg.Timestamp,
                // First record of the run wins, matching how the chat UI derives
                // a block id in rebuildBlocks().
                MessageUid = msg.MessageUid,
            });
        }

        return result;
    }

    internal static string StripInjectedTags(string content)
    {
        content = NovaContextTag.Replace(content, "");
        content = NovaPriorTag.Replace(content, "");
        return content.TrimStart();
    }

    private static void AppendParts(StringBuilder sb, string partsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(partsJson);
            foreach (var part in doc.RootElement.EnumerateArray())
            {
                var type = part.GetProperty("type").GetString();
                var content = part.TryGetProperty("content", out var c) ? c.GetString() : null;

                switch (type)
                {
                    case "text":
                        if (!string.IsNullOrWhiteSpace(content))
                            sb.AppendLine(content);
                        break;

                    case "tool_use":
                        var toolName = part.TryGetProperty("toolName", out var tn) ? tn.GetString() : "?";
                        var toolInput = part.TryGetProperty("toolInput", out var ti) ? ti.GetString() : "";
                        sb.AppendLine($"[tool: {toolName}({Truncate(toolInput, 120)})]");
                        break;

                    case "tool_result":
                        sb.AppendLine($"[result: {Truncate(content, 200)}]");
                        break;

                    case "image":
                        AppendImagePart(sb, part);
                        break;
                }
            }
        }
        catch
        {
            sb.AppendLine("[parts could not be parsed]");
        }
    }

    internal static void AppendImageParts(StringBuilder sb, string? partsJson)
    {
        if (string.IsNullOrEmpty(partsJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(partsJson);
            foreach (var part in doc.RootElement.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "image")
                    AppendImagePart(sb, part);
            }
        }
        catch
        {
            sb.AppendLine("[image attachment could not be parsed]");
        }
    }

    private static void AppendImagePart(StringBuilder sb, JsonElement part)
    {
        var url = part.TryGetProperty("url", out var u) ? u.GetString() : null;
        var assetId = part.TryGetProperty("assetId", out var a) ? a.GetString() : null;
        var reference = url ?? (assetId is not null ? $"/api/assets/{assetId}" : null);
        sb.AppendLine(reference is not null
            ? $"![attached image]({reference})"
            : "[image attachment]");
    }

    private static string? Truncate(string? s, int max) =>
        s is { Length: > 0 } && s.Length > max ? s[..max] + "..." : s;

    internal sealed class CollapsedMessage
    {
        public string Role { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? Content { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Source { get; set; }
        public string? MessageUid { get; set; }
    }
}
