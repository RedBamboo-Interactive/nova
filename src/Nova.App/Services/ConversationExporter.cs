using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RedBamboo.AppHost.Auth;
using Nova.App.Data;

namespace Nova.App.Services;

public static class ConversationExporter
{
    private static HttpClient RedCompute = new()
    {
        BaseAddress = new Uri(App.Config.Suite.RedCompute),
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static void Initialize(AuthenticatedHttpClientFactory factory)
    {
        RedCompute = factory.CreateClient(App.Config.Suite.RedCompute, TimeSpan.FromSeconds(15));
    }

    private static readonly Regex NovaContextTag = new(
        @"<nova-context[^>]*>[\s\S]*?</nova-context>\s*", RegexOptions.Compiled);

    private static readonly Regex NovaPriorTag = new(
        @"<nova-prior-messages[^>]*>[\s\S]*?</nova-prior-messages>\s*", RegexOptions.Compiled);

    public static async Task<string> ExportAsync(NovaDbContext db, DateTime since, int maxDiscussions = 50, string? userId = null)
    {
        maxDiscussions = Math.Clamp(maxDiscussions, 1, 200);

        var query = db.Discussions.Where(d => d.LastActivity >= since).WhereCanAccess(userId);

        var discussions = await query
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
            var sessionMessages = disc.SessionId is not null
                ? await FetchSessionMessages(disc.SessionId)
                : null;

            if (sessionMessages is { Count: > 0 })
                AppendSessionExport(sb, disc, sessionMessages);
            else
                await AppendLocalExport(sb, disc, db);

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Fetch a RedCompute session's status and raw message list.
    /// Returns null when RedCompute is unreachable or the session does not exist.
    /// </summary>
    public static async Task<SessionSnapshot?> FetchSessionAsync(string sessionId)
    {
        try
        {
            var resp = await RedCompute.GetAsync($"/ai-session/sessions/{sessionId}");
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            string? status = null;
            if (doc.RootElement.TryGetProperty("session", out var session)
                && session.TryGetProperty("status", out var st))
                status = st.GetString();

            var messages = new List<SessionMessage>();
            if (doc.RootElement.TryGetProperty("messages", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    messages.Add(new SessionMessage
                    {
                        Role = el.GetProperty("role").GetString() ?? "unknown",
                        EventType = el.TryGetProperty("eventType", out var et) ? et.GetString() ?? "text" : "text",
                        Content = el.TryGetProperty("content", out var c) ? c.GetString() : null,
                        Timestamp = el.TryGetProperty("timestamp", out var ts) ? ts.GetDateTime() : DateTime.MinValue,
                    });
                }
            }

            return new SessionSnapshot(status, messages);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> ExportSingleAsync(NovaDbContext db, Data.Entities.Discussion disc)
    {
        var sb = new StringBuilder();

        var sessionMessages = disc.SessionId is not null
            ? await FetchSessionMessages(disc.SessionId)
            : null;

        if (sessionMessages is { Count: > 0 })
            AppendSessionExport(sb, disc, sessionMessages);
        else
            await AppendLocalExport(sb, disc, db);

        return sb.ToString();
    }

    private static async Task<List<SessionMessage>?> FetchSessionMessages(string sessionId)
    {
        var snapshot = await FetchSessionAsync(sessionId);
        return snapshot is { Messages.Count: > 0 } ? snapshot.Messages : null;
    }

    private static void AppendSessionExport(StringBuilder sb, Data.Entities.Discussion disc, List<SessionMessage> raw)
    {
        var collapsed = CollapseMessages(raw);
        var textMessages = collapsed.Where(m => m.EventType == "text" && !string.IsNullOrWhiteSpace(m.Content)).ToList();

        sb.AppendLine($"## {disc.Title ?? "Untitled"} [{disc.Id}]");
        sb.AppendLine($"Created: {disc.CreatedAt:yyyy-MM-dd HH:mm} — {textMessages.Count} message(s)");
        sb.AppendLine();

        foreach (var msg in textMessages)
        {
            var role = msg.Role == "user" ? "user" : "nova";
            sb.AppendLine($"**{role}** ({msg.Timestamp:HH:mm}):");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }
    }

    private static List<CollapsedMessage> CollapseMessages(List<SessionMessage> raw)
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
            });
        }

        return result;
    }

    private static string StripInjectedTags(string content)
    {
        content = NovaContextTag.Replace(content, "");
        content = NovaPriorTag.Replace(content, "");
        return content.TrimStart();
    }

    private static async Task AppendLocalExport(StringBuilder sb, Data.Entities.Discussion disc, NovaDbContext db)
    {
        var messages = await db.Conversations
            .Where(m => m.ContextId == disc.Id)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();

        sb.AppendLine($"## {disc.Title ?? "Untitled"} [{disc.Id}]");
        sb.AppendLine($"Created: {disc.CreatedAt:yyyy-MM-dd HH:mm} — {messages.Count} message(s) [events only, session expired]");
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

    private class CollapsedMessage
    {
        public string Role { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? Content { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

/// <summary>A single raw message from a RedCompute session transcript.</summary>
public class SessionMessage
{
    public string Role { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? Content { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>Point-in-time view of a RedCompute session: its status plus the raw message list.</summary>
public record SessionSnapshot(string? Status, List<SessionMessage> Messages);
