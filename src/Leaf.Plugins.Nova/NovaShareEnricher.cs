using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed class NovaShareEnricher(
    DiscussionStore store,
    AgentDirectory agents,
    RedComputeClient redCompute,
    IDiscussions discussions) : IShareEnricher
{
    private static readonly Regex ContextTag = new(
        @"<nova-context[^>]*>[\s\S]*?</nova-context>\s*", RegexOptions.Compiled);

    private static readonly Regex PriorTag = new(
        @"<nova-prior-message[s]?[^>]*>[\s\S]*?</nova-prior-message[s]?>\s*", RegexOptions.Compiled);

    public async Task<ShareCustomization?> EnrichAsync(Guid discussionEntityId, CancellationToken ct = default)
    {
        var allDiscussions = await store.ListAsync(ct: ct);
        var disc = allDiscussions.FirstOrDefault(d => d.EntityId == discussionEntityId);
        if (disc is null) return null;

        string? agentName = null;
        string? avatarBase64 = null;
        string? avatarMediaType = null;

        if (disc.AgentId is not null)
        {
            var agent = await agents.GetAgentAsync(disc.AgentId, ct);
            agentName = agent?.Name;

            if (agent?.AvatarFilename is not null)
            {
                var avatarPath = ResolveAvatarPath(agent.AvatarFilename);
                if (avatarPath != null && File.Exists(avatarPath))
                {
                    var bytes = await File.ReadAllBytesAsync(avatarPath, ct);
                    avatarBase64 = Convert.ToBase64String(bytes);
                    avatarMediaType = Path.GetExtension(avatarPath).ToLowerInvariant() switch
                    {
                        ".webp" => "image/webp",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        _ => "image/png",
                    };
                }
            }
        }

        var messages = await BuildMessagesAsync(disc, agentName, ct);

        return new ShareCustomization
        {
            AgentName = agentName,
            AvatarBase64 = avatarBase64,
            AvatarMediaType = avatarMediaType,
            AppName = "Nova",
            Messages = messages,
        };
    }

    private async Task<List<ShareableMessage>> BuildMessagesAsync(DiscussionRead disc, string? agentName, CancellationToken ct)
    {
        var messages = new List<ShareableMessage>();

        var snapshot = disc.SessionId is not null
            ? await redCompute.GetSessionAsync(disc.SessionId, ct)
            : null;

        if (snapshot is { Messages.Count: > 0 })
        {
            string? pendingRole = null;
            var textBuffer = new System.Text.StringBuilder();
            var parts = new List<string>();
            DateTime pendingTimestamp = default;
            int toolCount = 0;

            void FlushText()
            {
                if (textBuffer.Length > 0)
                {
                    parts.Add(textBuffer.ToString());
                    textBuffer.Clear();
                }
            }

            void FlushTools()
            {
                if (toolCount > 0)
                {
                    parts.Add($"{{{{tools:{toolCount}}}}}");
                    toolCount = 0;
                }
            }

            void FlushPending()
            {
                FlushText();
                FlushTools();
                if (pendingRole == null || parts.Count == 0) return;
                messages.Add(new ShareableMessage
                {
                    Role = pendingRole == "user" ? "user" : "assistant",
                    Content = string.Join("\n\n", parts),
                    Timestamp = pendingTimestamp.ToString("o"),
                    SenderName = pendingRole != "user" ? agentName : null,
                });
                parts.Clear();
                pendingRole = null;
            }

            foreach (var msg in snapshot.Messages)
            {
                if (msg.EventType is "thinking" or "status") continue;

                if (msg.EventType == "tool_result") continue;
                if (msg.EventType == "tool_use")
                {
                    FlushText();
                    toolCount++;
                    pendingRole ??= msg.Role;
                    if (parts.Count == 0 && textBuffer.Length == 0) pendingTimestamp = msg.Timestamp;
                    continue;
                }
                if (msg.EventType != "text") continue;

                var content = msg.Role == "user" ? StripInjectedTags(msg.Content ?? "") : msg.Content ?? "";
                if (string.IsNullOrWhiteSpace(content)) continue;

                if (pendingRole != null && pendingRole != msg.Role)
                    FlushPending();

                FlushTools();
                pendingRole ??= msg.Role;
                if (parts.Count == 0 && textBuffer.Length == 0) pendingTimestamp = msg.Timestamp;
                textBuffer.Append(content);
            }

            FlushPending();
        }
        else
        {
            var records = await discussions.GetMessagesAsync(disc.EntityId, ct: ct);
            foreach (var m in records)
            {
                var source = m.Metadata["source"]?.GetValue<string>() ?? "";
                if (source.StartsWith("event:")) continue;

                var content = ExtractTextContent(m);
                if (string.IsNullOrWhiteSpace(content)) continue;

                messages.Add(new ShareableMessage
                {
                    Role = m.Role == "user" ? "user" : "assistant",
                    Content = content,
                    Timestamp = m.CreatedAt.UtcDateTime.ToString("o"),
                    SenderName = m.Role != "user" ? agentName : null,
                });
            }
        }

        return messages;
    }

    private static string ExtractTextContent(DiscussionMessage m)
    {
        var partsJson = m.Metadata["parts_json"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(partsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(partsJson);
                var texts = new List<string>();
                foreach (var part in doc.RootElement.EnumerateArray())
                {
                    if (part.GetProperty("type").GetString() != "text") continue;
                    if (part.TryGetProperty("content", out var c) && c.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                        texts.Add(s);
                }
                return StripInjectedTags(string.Join("\n\n", texts));
            }
            catch { }
        }
        return StripInjectedTags(m.Content);
    }

    private static string StripInjectedTags(string content)
    {
        content = ContextTag.Replace(content, "");
        content = PriorTag.Replace(content, "");
        return content.TrimStart();
    }

    private static string? ResolveAvatarPath(string avatarFilename)
    {
        var name = avatarFilename;
        if (name.StartsWith("/api/assets/"))
            name = name["/api/assets/".Length..];
        if (name.StartsWith('/') || name.Contains("://"))
            return null;

        var assetsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedLeaf", "assets");
        var path = Path.Combine(assetsDir, name);
        return File.Exists(path) ? path : null;
    }
}
