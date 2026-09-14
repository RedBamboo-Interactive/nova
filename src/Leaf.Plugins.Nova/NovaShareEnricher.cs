using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed class NovaShareEnricher(
    DiscussionStore store,
    AgentDirectory agents,
    RedComputeClient redCompute,
    IDiscussions discussions,
    IAssets assets) : IShareEnricher
{
    private static readonly Regex ContextTag = new(
        @"<nova-context[^>]*>[\s\S]*?</nova-context>\s*", RegexOptions.Compiled);

    private static readonly Regex PriorTag = new(
        @"<nova-prior-message[s]?[^>]*>[\s\S]*?</nova-prior-message[s]?>\s*", RegexOptions.Compiled);

    private static readonly Regex MarkdownImage = new(
        """!\[(?<alt>[^\]]*)\]\(\s*(?:<(?<angled>[^>]+)>|(?<plain>[^)\s]+))\s*\)""",
        RegexOptions.Compiled);

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
        var records = await discussions.GetMessagesAsync(disc.EntityId, ct: ct);
        var userPartsByUid = records
            .Where(m => m.Role == "user")
            .Where(m => !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>()))
            .GroupBy(m => m.Metadata["uid"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(m => m.Metadata["source"]?.GetValue<string>() == "user-message")
                    .ThenByDescending(m => m.CreatedAt)
                    .First()
                    .Metadata["parts_json"]?.GetValue<string>(),
                StringComparer.Ordinal);

        var snapshot = disc.SessionId is not null
            ? await redCompute.GetSessionAsync(disc.SessionId, ct)
            : null;

        if (snapshot is { Messages.Count: > 0 })
        {
            string? pendingRole = null;
            var textBuffer = new System.Text.StringBuilder();
            var parts = new List<string>();
            var pendingImages = new List<ShareableImage>();
            var imageUids = new HashSet<string>(StringComparer.Ordinal);
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
                if (pendingRole == null || (parts.Count == 0 && pendingImages.Count == 0)) return;
                messages.Add(new ShareableMessage
                {
                    Role = pendingRole == "user" ? "user" : "assistant",
                    Content = string.Join("\n\n", parts),
                    Timestamp = pendingTimestamp.ToString("o"),
                    SenderName = pendingRole != "user" ? agentName : null,
                    Images = pendingImages.Count > 0 ? [.. pendingImages] : null,
                });
                parts.Clear();
                pendingImages.Clear();
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
                List<ShareableImage> messageImages = [];
                if (msg.Role == "user" && (msg.MessageUid is null || imageUids.Add(msg.MessageUid)))
                {
                    userPartsByUid.TryGetValue(msg.MessageUid ?? "", out var persistedParts);
                    messageImages = await ResolveShareImagesAsync(msg.AttachmentsJson, persistedParts, ct);
                }
                else if (msg.Role != "user")
                {
                    var assistantContent = await ResolveAssistantShareContentAsync(content, ct);
                    content = assistantContent.Content;
                    messageImages = assistantContent.Images;
                }
                if (string.IsNullOrWhiteSpace(content) && messageImages.Count == 0) continue;

                if (pendingRole != null && pendingRole != msg.Role)
                    FlushPending();

                FlushTools();
                pendingRole ??= msg.Role;
                if (parts.Count == 0 && textBuffer.Length == 0) pendingTimestamp = msg.Timestamp;
                textBuffer.Append(content);
                pendingImages.AddRange(messageImages);
            }

            FlushPending();
        }
        else
        {
            foreach (var m in records)
            {
                var source = m.Metadata["source"]?.GetValue<string>() ?? "";
                if (source.StartsWith("event:")) continue;

                var content = ExtractTextContent(m);
                List<ShareableImage> images;
                if (m.Role == "user")
                {
                    images = await ResolveShareImagesAsync(
                        transcriptJson: null,
                        m.Metadata["parts_json"]?.GetValue<string>(), ct);
                }
                else
                {
                    var assistantContent = await ResolveAssistantShareContentAsync(content, ct);
                    content = assistantContent.Content;
                    images = assistantContent.Images;
                }
                if (string.IsNullOrWhiteSpace(content) && images.Count == 0) continue;

                messages.Add(new ShareableMessage
                {
                    Role = m.Role == "user" ? "user" : "assistant",
                    Content = content,
                    Timestamp = m.CreatedAt.UtcDateTime.ToString("o"),
                    SenderName = m.Role != "user" ? agentName : null,
                    Images = images.Count > 0 ? images : null,
                });
            }
        }

        return messages;
    }

    internal async Task<List<ShareableImage>> ResolveShareImagesAsync(
        string? transcriptJson, string? persistedPartsJson, CancellationToken ct)
    {
        var sources = ExtractShareImageSources(transcriptJson);
        if (sources.Count == 0)
            sources = ExtractShareImageSources(persistedPartsJson);

        var result = new List<ShareableImage>(sources.Count);
        foreach (var source in sources)
        {
            byte[] bytes;
            string mediaType;
            if (source.Kind == "inline")
            {
                try { bytes = Convert.FromBase64String(source.Base64!); }
                catch (FormatException)
                {
                    throw new ShareSnapshotException("invalid_share_image",
                        "An attached image could not be decoded for sharing");
                }
                mediaType = source.MediaType ?? "image/png";
            }
            else if (source.Kind == "asset")
            {
                var file = await assets.ReadAsync(source.Id!, ct)
                    ?? throw new ShareSnapshotException("share_image_unavailable",
                        "An attached image is no longer available in RedLeaf");
                bytes = file.Bytes;
                mediaType = file.ContentType;
            }
            else
            {
                var file = await redCompute.GetInputAttachmentAsync(source.Id!, ct)
                    ?? throw new ShareSnapshotException("share_image_unavailable",
                        "An attached image is no longer available in RedCompute");
                bytes = file.Bytes;
                mediaType = file.MediaType == "application/octet-stream"
                    ? source.MediaType ?? file.MediaType
                    : file.MediaType;
            }

            if (!IsShareableImageType(mediaType))
                throw new ShareSnapshotException("invalid_share_image",
                    $"Attached image media type '{mediaType}' cannot be shared");

            result.Add(new ShareableImage
            {
                Base64 = Convert.ToBase64String(bytes),
                MediaType = mediaType,
                AltText = source.AltText,
            });
        }
        return result;
    }

    internal async Task<AssistantShareContent> ResolveAssistantShareContentAsync(
        string content, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(content)) return new AssistantShareContent(content, []);

        var images = new List<ShareableImage>();
        var rewritten = new System.Text.StringBuilder(content.Length);
        var copiedThrough = 0;

        foreach (Match match in MarkdownImage.Matches(content))
        {
            var url = match.Groups["angled"].Success
                ? match.Groups["angled"].Value
                : match.Groups["plain"].Value;
            var assetId = AssetIdFromAssistantImageUrl(url);
            var removeFromPublicText = assetId is not null || IsPrivateImageReference(url);
            if (!removeFromPublicText) continue;

            rewritten.Append(content, copiedThrough, match.Index - copiedThrough);
            copiedThrough = match.Index + match.Length;

            if (assetId is null) continue;

            var file = await assets.ReadAsync(assetId, ct)
                ?? throw new ShareSnapshotException("share_image_unavailable",
                    "An image authored in the discussion is no longer available in RedLeaf");
            if (!IsShareableImageType(file.ContentType))
                throw new ShareSnapshotException("invalid_share_image",
                    $"Authored image media type '{file.ContentType}' cannot be shared");

            images.Add(new ShareableImage
            {
                Base64 = Convert.ToBase64String(file.Bytes),
                MediaType = file.ContentType,
                AltText = string.IsNullOrWhiteSpace(match.Groups["alt"].Value)
                    ? "Shared image"
                    : match.Groups["alt"].Value,
            });
        }

        if (copiedThrough == 0) return new AssistantShareContent(content, images);

        rewritten.Append(content, copiedThrough, content.Length - copiedThrough);
        return new AssistantShareContent(rewritten.ToString().Trim(), images);
    }

    internal static List<ShareImageSource> ExtractShareImageSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var result = new List<ShareImageSource>();
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                ExtractImageParts(doc.RootElement, result);
            else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("attachments", out var attachments)
                    && attachments.ValueKind == System.Text.Json.JsonValueKind.Array)
                    ExtractImageParts(attachments, result);
                if (doc.RootElement.TryGetProperty("images", out var images)
                    && images.ValueKind == System.Text.Json.JsonValueKind.Array)
                    ExtractLegacyImages(images, result);
            }
            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ShareSnapshotException("invalid_share_image",
                "Attached image metadata could not be parsed for sharing");
        }
    }

    private static void ExtractImageParts(
        System.Text.Json.JsonElement parts, List<ShareImageSource> result)
    {
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var type = part.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() : null;
            var kind = part.TryGetProperty("kind", out var kindElement)
                ? kindElement.GetString() : null;
            var mediaType = part.TryGetProperty("mediaType", out var mediaElement)
                ? mediaElement.GetString() : null;
            var name = part.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() : null;

            if (type == "image")
            {
                var assetId = part.TryGetProperty("assetId", out var assetElement)
                    ? assetElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(assetId)
                    && part.TryGetProperty("url", out var urlElement))
                    assetId = AssetIdFromUrl(urlElement.GetString());
                if (!string.IsNullOrWhiteSpace(assetId))
                    result.Add(new ShareImageSource("asset", assetId, null, mediaType, "Attached image"));
                continue;
            }

            if ((type == "attachment" || type is null)
                && string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                var id = part.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(new ShareImageSource("attachment", id, null, mediaType,
                        string.IsNullOrWhiteSpace(name) ? "Attached image" : name));
            }
        }
    }

    private static void ExtractLegacyImages(
        System.Text.Json.JsonElement images, List<ShareImageSource> result)
    {
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var base64 = image.TryGetProperty("base64", out var data) ? data.GetString() : null;
            if (string.IsNullOrWhiteSpace(base64)) continue;
            var mediaType = image.TryGetProperty("mediaType", out var media)
                ? media.GetString() : "image/png";
            result.Add(new ShareImageSource("inline", null, base64, mediaType, "Attached image"));
        }
    }

    private static string? AssetIdFromUrl(string? url)
    {
        const string prefix = "/api/assets/";
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = url[prefix.Length..];
        return id.Length > 0 && !id.Contains('/') && !id.Contains('?') && !id.Contains('#') ? id : null;
    }

    private static string? AssetIdFromAssistantImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var direct = AssetIdFromUrl(url.Trim());
        if (direct is not null) return direct;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback
            || uri.Port != 18804)
            return null;

        return AssetIdFromUrl(uri.AbsolutePath);
    }

    private static bool IsPrivateImageReference(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var value = url.Trim();
        if (value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || Regex.IsMatch(value, @"^/?[a-zA-Z]:[\\/]", RegexOptions.CultureInvariant))
            return true;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.IsFile || (uri.Scheme is "http" or "https" && uri.IsLoopback));
    }

    private static bool IsShareableImageType(string mediaType) => mediaType.ToLowerInvariant() is
        "image/png" or "image/jpeg" or "image/gif" or "image/webp";

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

    internal sealed record ShareImageSource(
        string Kind, string? Id, string? Base64, string? MediaType, string AltText);

    internal sealed record AssistantShareContent(string Content, List<ShareableImage> Images);
}
