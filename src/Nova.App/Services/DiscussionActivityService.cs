using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Nova.App.Services;

public static class DiscussionActivityService
{
    private static readonly Regex XmlTags = new(@"<nova-\w+[\s\S]*?</nova-\w+>\s*", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, DateTime> _lastNovaEvent = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    public static async Task OnUserMessage(string discussionId, string? title, string contentPreview)
    {
        if (IsLiveDiscussion(discussionId)) return;
        if (string.IsNullOrEmpty(title)) return;
        var preview = Truncate(contentPreview, 80);
        if (preview == "…") return;
        await PostSafe("discussion", $"Laurent in \"{title}\": {preview}");
    }

    public static async Task OnNovaMessage(string discussionId, string? title, string contentPreview)
    {
        if (IsLiveDiscussion(discussionId)) return;
        if (string.IsNullOrEmpty(title)) return;
        if (!TryThrottle(discussionId)) return;
        var preview = Truncate(contentPreview, 80);
        if (preview == "…") return;
        await PostSafe("discussion", $"Nova in \"{title}\": {preview}");
    }

    public static async Task OnArchived(string discussionId, string? title)
    {
        if (IsLiveDiscussion(discussionId)) return;
        if (string.IsNullOrEmpty(title)) return;
        await PostSafe("discussion", $"\"{title}\" archived");
    }

    private static bool TryThrottle(string discussionId)
    {
        var now = DateTime.UtcNow;
        if (_lastNovaEvent.TryGetValue(discussionId, out var last) && now - last < Cooldown)
            return false;
        _lastNovaEvent[discussionId] = now;
        return true;
    }

    private static bool IsLiveDiscussion(string discussionId)
    {
        return LiveEventService.Instance?.DiscussionId == discussionId;
    }

    private static string Truncate(string s, int max)
    {
        s = XmlTags.Replace(s, "");
        s = s.Replace("\n", " ").Replace("\r", "").Trim();
        if (string.IsNullOrEmpty(s)) return "…";
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static async Task PostSafe(string source, string content)
    {
        try
        {
            var live = LiveEventService.Instance;
            if (live == null) return;
            await live.PostAsync(source, content);
        }
        catch { }
    }
}
