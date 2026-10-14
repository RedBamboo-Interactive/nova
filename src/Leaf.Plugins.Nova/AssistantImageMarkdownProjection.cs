using System.Text;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Nova;

internal sealed record AssistantImageReference(string AssetId, string Description);

internal sealed record AssistantImageProjection(
    string Content,
    IReadOnlyList<AssistantImageReference> Images);

internal static class AssistantImageMarkdownProjection
{
    private static readonly Regex MarkdownImage = new(
        """!\[(?<alt>[^\]]*)\]\(\s*(?:<(?<angled>[^>]+)>|(?<plain>[^)\s]+))\s*\)""",
        RegexOptions.Compiled);

    public static AssistantImageProjection Project(string content)
    {
        if (string.IsNullOrEmpty(content)) return new AssistantImageProjection(content, []);

        var images = new List<AssistantImageReference>();
        var rewritten = new StringBuilder(content.Length);
        var copiedThrough = 0;

        foreach (Match match in MarkdownImage.Matches(content))
        {
            var url = match.Groups["angled"].Success
                ? match.Groups["angled"].Value
                : match.Groups["plain"].Value;
            var assetId = AssetIdFromAssistantImageUrl(url);
            var removeFromDeliveryText = assetId is not null || IsPrivateImageReference(url);
            if (!removeFromDeliveryText) continue;

            rewritten.Append(content, copiedThrough, match.Index - copiedThrough);
            copiedThrough = match.Index + match.Length;

            if (assetId is not null)
                images.Add(new AssistantImageReference(
                    assetId,
                    string.IsNullOrWhiteSpace(match.Groups["alt"].Value)
                        ? "Shared image"
                        : match.Groups["alt"].Value));
        }

        if (copiedThrough == 0) return new AssistantImageProjection(content, images);

        rewritten.Append(content, copiedThrough, content.Length - copiedThrough);
        return new AssistantImageProjection(rewritten.ToString().Trim(), images);
    }

    private static string? AssetIdFromUrl(string? url)
    {
        const string prefix = "/api/assets/";
        if (string.IsNullOrWhiteSpace(url)
            || !url.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var id = url[prefix.Length..];
        return id.Length > 0 && !id.Contains('/') && !id.Contains('?') && !id.Contains('#')
            ? id
            : null;
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
}
