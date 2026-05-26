using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nova.App.Data;

namespace Nova.App.Services;

public static class ConversationExporter
{
    public static async Task<string> ExportAsync(NovaDbContext db, DateTime since, int maxDiscussions = 50)
    {
        maxDiscussions = Math.Clamp(maxDiscussions, 1, 200);

        var discussions = await db.Discussions
            .Where(d => d.LastActivity >= since)
            .OrderByDescending(d => d.LastActivity)
            .Take(maxDiscussions)
            .ToListAsync();

        if (discussions.Count == 0)
            return $"# No conversations since {since:yyyy-MM-dd}\n";

        var sb = new StringBuilder();
        sb.AppendLine($"# Conversations since {since:yyyy-MM-dd}");
        sb.AppendLine($"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {discussions.Count} discussion(s)");
        sb.AppendLine();

        foreach (var disc in discussions)
        {
            var messages = await db.Conversations
                .Where(m => m.ContextId == disc.Id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            sb.AppendLine($"## {disc.Title ?? "Untitled"} [{disc.Id}]");
            sb.AppendLine($"Created: {disc.CreatedAt:yyyy-MM-dd HH:mm} — {messages.Count} message(s)");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                var role = msg.Role == "user" ? "user" : "nova";
                sb.AppendLine($"**{role}** ({msg.Timestamp:HH:mm}):");

                if (!string.IsNullOrEmpty(msg.PartsJson))
                    AppendParts(sb, msg.PartsJson);
                else
                    sb.AppendLine(msg.Content);

                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
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
                }
            }
        }
        catch
        {
            sb.AppendLine("[parts could not be parsed]");
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is { Length: > 0 } && s.Length > max ? s[..max] + "..." : s;
}
