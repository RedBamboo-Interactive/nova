using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nova.App.Data.Entities;

namespace Nova.App.Services;

public sealed class ContextSnapshot
{
    [JsonPropertyName("discussions")]
    public List<DiscussionEntry> Discussions { get; set; } = [];

    [JsonPropertyName("otherAgentDiscussions")]
    public List<DiscussionEntry> OtherAgentDiscussions { get; set; } = [];

    [JsonPropertyName("outfit")]
    public string? Outfit { get; set; }

    [JsonPropertyName("outfitAsset")]
    public string? OutfitAsset { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("mood")]
    public string? Mood { get; set; }

    public sealed class DiscussionEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("messageCount")]
        public int MessageCount { get; set; }

        [JsonPropertyName("lastActivity")]
        public DateTime LastActivity { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "idle";
    }
}

public static class NovaContextBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ContextSnapshot BuildSnapshot(
        List<Discussion> ownDiscussions,
        List<Discussion>? otherAgentDiscussions,
        string? outfit,
        string? outfitAsset,
        string? location = null,
        string? mood = null)
    {
        return new ContextSnapshot
        {
            Discussions = ownDiscussions.Select(d => new ContextSnapshot.DiscussionEntry
            {
                Id = d.Id,
                Title = d.Title,
                MessageCount = d.MessageCount,
                Status = d.Status,
                LastActivity = d.LastActivity,
            }).ToList(),
            OtherAgentDiscussions = otherAgentDiscussions?.Select(d => new ContextSnapshot.DiscussionEntry
            {
                Id = d.Id,
                Title = d.Title,
                MessageCount = d.MessageCount,
                Status = d.Status,
                LastActivity = d.LastActivity,
            }).ToList() ?? [],
            Outfit = outfit,
            OutfitAsset = outfitAsset,
            Location = location,
            Mood = mood,
        };
    }

    public static string BuildFullContext(
        ContextSnapshot snapshot,
        string currentId, DateTime now, ResolvedDevice device, string input, string? agentName)
    {
        var sb = new StringBuilder();
        AppendOpenTag(sb, now, device, input, currentId, agentName, snapshot.Location);

        if (snapshot.Mood != null)
            sb.Append($"\nMood: {snapshot.Mood}");
        else
            sb.Append("\nRead memory/dreaming/mood.md for your current emotional state before responding.");

        var active = snapshot.Discussions.Where(d => d.Status != "archived").ToList();
        var archived = snapshot.Discussions.Where(d => d.Status == "archived" && d.MessageCount > 0).ToList();

        if (active.Count > 0)
        {
            sb.Append("\nActive discussions:");
            foreach (var d in active)
            {
                var marker = d.Id == currentId ? " <- you are here" : "";
                sb.Append($"\n- [{d.Id}] \"{d.Title ?? "(untitled)"}\" . {d.MessageCount} msgs . {FormatRelativeTime(now - d.LastActivity)}{marker}");
            }
        }

        if (archived.Count > 0)
        {
            sb.Append("\n\nRecently archived:");
            foreach (var d in archived)
                sb.Append($"\n- [{d.Id}] \"{d.Title ?? "(untitled)"}\" . {d.MessageCount} msgs . archived {FormatRelativeTime(now - d.LastActivity)}");
        }

        if (snapshot.OtherAgentDiscussions.Count > 0)
        {
            sb.Append("\n\nOther agents' active discussions:");
            foreach (var d in snapshot.OtherAgentDiscussions)
                sb.Append($"\n- [{d.Id}] \"{d.Title ?? "(untitled)"}\" . {d.MessageCount} msgs . {FormatRelativeTime(now - d.LastActivity)}");
        }

        sb.Append("\n\nRecall any discussion: curl -s http://127.0.0.1:18803/api/discussions/{id}/export");
        sb.Append("\nSearch all discussions: curl -s \"http://127.0.0.1:18803/api/discussions/search?q={query}\"");
        sb.Append("\nGet full context snapshot: curl -s http://127.0.0.1:18803/api/discussions/{id}/context");

        if (snapshot.Outfit != null)
            sb.Append($"\n\n{snapshot.Outfit}");

        sb.Append("\n</nova-context>");
        return sb.ToString();
    }

    public static string BuildDeltaContext(
        ContextSnapshot current, ContextSnapshot previous,
        string currentId, DateTime now, ResolvedDevice device, string input, string? agentName)
    {
        var changes = new List<string>();

        var prevIds = previous.Discussions.Select(d => d.Id).ToHashSet();
        var currIds = current.Discussions.Select(d => d.Id).ToHashSet();
        var prevMap = previous.Discussions.ToDictionary(d => d.Id);
        var currMap = current.Discussions.ToDictionary(d => d.Id);

        // New discussions
        foreach (var id in currIds.Except(prevIds))
        {
            var d = currMap[id];
            changes.Add($"New discussion [{d.Id}] \"{d.Title ?? "(untitled)"}\" ({d.Status})");
        }

        // Removed discussions (deleted or fell out of window)
        foreach (var id in prevIds.Except(currIds))
        {
            var d = prevMap[id];
            changes.Add($"Discussion [{d.Id}] \"{d.Title ?? "(untitled)"}\" no longer visible");
        }

        // Status changes and message count changes
        foreach (var id in currIds.Intersect(prevIds))
        {
            var prev = prevMap[id];
            var curr = currMap[id];

            if (prev.Status != curr.Status)
                changes.Add($"Discussion [{id}] \"{curr.Title ?? "(untitled)"}\" changed status: {prev.Status} -> {curr.Status}");

            var newMsgs = curr.MessageCount - prev.MessageCount;
            if (newMsgs > 0)
                changes.Add($"{newMsgs} new message{(newMsgs > 1 ? "s" : "")} in [{id}] \"{curr.Title ?? "(untitled)"}\"");

            if (prev.Title != curr.Title && curr.Title != null)
                changes.Add($"Discussion [{id}] renamed to \"{curr.Title}\"");
        }

        // Other agents' discussions
        var prevOther = previous.OtherAgentDiscussions.Select(d => d.Id).ToHashSet();
        var currOther = current.OtherAgentDiscussions.Select(d => d.Id).ToHashSet();
        var prevOtherMap = previous.OtherAgentDiscussions.ToDictionary(d => d.Id);
        var currOtherMap = current.OtherAgentDiscussions.ToDictionary(d => d.Id);

        foreach (var id in currOther.Except(prevOther))
        {
            var d = currOtherMap[id];
            changes.Add($"New discussion from another agent: [{d.Id}] \"{d.Title ?? "(untitled)"}\"");
        }

        foreach (var id in prevOther.Except(currOther))
        {
            var d = prevOtherMap[id];
            changes.Add($"Other agent discussion [{d.Id}] \"{d.Title ?? "(untitled)"}\" no longer active");
        }

        // Outfit
        if (current.Outfit != previous.Outfit)
        {
            if (current.Outfit != null)
                changes.Add($"Outfit changed: {current.Outfit}");
            else if (previous.Outfit != null)
                changes.Add("Outfit removed");
        }

        // Location
        if (current.Location != previous.Location && current.Location != null)
            changes.Add($"Location changed: {current.Location}");

        // Mood
        if (current.Mood != previous.Mood && current.Mood != null)
            changes.Add($"Mood changed: {current.Mood}");

        var sb = new StringBuilder();
        AppendOpenTag(sb, now, device, input, currentId, agentName, current.Location);

        if (changes.Count > 0)
        {
            sb.Append("\nChanges since last message:");
            foreach (var c in changes)
                sb.Append($"\n- {c}");
        }
        else
        {
            sb.Append("\nNo context changes since last message.");
        }

        sb.Append("\n</nova-context>");
        return sb.ToString();
    }

    public static string SerializeSnapshot(ContextSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, JsonOpts);

    public static ContextSnapshot? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ContextSnapshot>(json, JsonOpts); }
        catch { return null; }
    }

    public static Dictionary<string, object?> BuildMetadata(
        ContextSnapshot snapshot, DateTime now, ResolvedDevice device, string input,
        string currentId, string? agentName)
    {
        var active = snapshot.Discussions.Where(d => d.Status != "archived").ToList();
        var archived = snapshot.Discussions.Where(d => d.Status == "archived").ToList();

        return new Dictionary<string, object?>
        {
            ["timestamp"] = now.ToString("o"),
            ["day"] = now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture),
            ["device"] = device.ShortLabel,
            ["deviceType"] = device.Type,
            ["devicePlatform"] = device.Platform,
            ["deviceRoom"] = device.Room,
            ["input"] = input,
            ["discussion"] = currentId,
            ["agent"] = agentName,
            ["activeDiscussionCount"] = active.Count,
            ["archivedDiscussionCount"] = archived.Count,
            ["otherAgentDiscussionCount"] = snapshot.OtherAgentDiscussions.Count,
            ["outfit"] = snapshot.Outfit,
        };
    }

    private static void AppendOpenTag(StringBuilder sb, DateTime now, ResolvedDevice device, string input, string currentId, string? agentName, string? userLocation = null)
    {
        var agentAttr = agentName != null ? $" agent=\"{agentName}\"" : "";
        var locationAttr = userLocation != null ? $" location=\"{userLocation}\"" : "";
        var roomAttr = device.Room != null ? $" room=\"{device.Room}\"" : "";
        sb.Append($"<nova-context timestamp=\"{now:yyyy-MM-ddTHH:mm:ssZ}\" day=\"{now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture)}\" device=\"{device.ShortLabel}\" device_type=\"{device.Type}\" platform=\"{device.Platform}\" input=\"{input}\" discussion=\"{currentId}\"{agentAttr}{locationAttr}{roomAttr}>");
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
