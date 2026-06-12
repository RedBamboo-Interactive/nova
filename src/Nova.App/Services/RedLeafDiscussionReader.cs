using System.Net.Http;
using System.Text.Json;
using RedBamboo.AppHost.Auth;

namespace Nova.App.Services;

/// <summary>
/// RedLeaf-backed discussion reads (read-path cutover): the discussion list,
/// message history, images and search come from `discussion` entities +
/// `nova-messages` records instead of the local SQLite tables. RedLeaf is a
/// hard dependency here by decision — failures propagate to the caller, no
/// local fallback. Writes stay on SQLite with the NovaMirror dual-write.
/// </summary>
public sealed class RedLeafDiscussionReader
{
    private readonly HttpClient _http;

    public RedLeafDiscussionReader(string redLeafBaseUrl, JwtService jwtService)
    {
        var token = jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
        _http = new HttpClient
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    public sealed record DiscussionRead(
        string Id, string? Title, string? SessionId, string Status,
        DateTime CreatedAt, DateTime LastActivity, int MessageCount,
        DateTime? LastReadAt, string? OwnerId, Guid EntityId);

    public sealed record MessageRead(
        long Id, string Role, string? Content, string? PartsJson, string? Source,
        string? DiscussionId, DateTime Timestamp);

    public async Task<List<DiscussionRead>> GetDiscussionsAsync()
    {
        // Entities can't be server-sorted by a data key; recent activity is a
        // subset of recently-updated (upserts bump UpdatedAt), so fetch the
        // most recently updated page and order client-side.
        using var doc = await GetJsonAsync(
            "api/entities?type=discussion&data.app=nova&sort_by=updated_at&sort_dir=desc&limit=500");
        var discussions = new List<DiscussionRead>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var d = MapDiscussion(item);
            if (d != null) discussions.Add(d);
        }
        discussions.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));
        return discussions;
    }

    public async Task<DiscussionRead?> GetDiscussionAsync(string discussionId)
    {
        var slug = NovaMirror.DiscussionSlug(discussionId);
        var response = await _http.GetAsync($"api/entities/{Uri.EscapeDataString(slug)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return MapDiscussion(doc.RootElement);
    }

    public async Task<List<MessageRead>> GetMessagesAsync(Guid entityId)
    {
        var messages = new List<MessageRead>();
        long afterId = 0;
        while (true)
        {
            using var doc = await GetJsonAsync(
                $"api/streams/nova-messages/records?entity_id={entityId}&order=asc&limit=1000&after_id={afterId}");
            var items = doc.RootElement.GetProperty("items");
            foreach (var rec in items.EnumerateArray())
            {
                var m = MapMessage(rec);
                afterId = m.Id;
                messages.Add(m);
            }
            if (items.GetArrayLength() < 1000) break;
        }
        return messages;
    }

    /// <summary>
    /// Newest-first substring search over message data (server-side ILIKE
    /// across the record JSON). Capped at 1000 records — match counts are
    /// approximate beyond that.
    /// </summary>
    public async Task<List<MessageRead>> SearchMessagesAsync(string query)
    {
        using var doc = await GetJsonAsync(
            $"api/streams/nova-messages/records?search={Uri.EscapeDataString(query)}&order=desc&limit=1000");
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(MapMessage).ToList();
    }

    private static DiscussionRead? MapDiscussion(JsonElement entity)
    {
        using var data = JsonDocument.Parse(entity.GetProperty("data").GetString()!);
        var d = data.RootElement;

        var id = Str(d, "discussion_id");
        if (id == null) return null;

        // Pre-cutover mirror writes lack data.title; fall back to the entity
        // name, normalizing its untitled placeholder back to null.
        var name = entity.GetProperty("name").GetString();
        var title = Str(d, "title") ?? (name == $"Discussion {id}" ? null : name);

        return new DiscussionRead(
            id, title, Str(d, "session_id"), Str(d, "status") ?? "stopped",
            ParseUtc(Str(d, "created_at")) ?? default,
            ParseUtc(Str(d, "last_activity")) ?? default,
            Int(d, "message_count") ?? 0,
            ParseUtc(Str(d, "last_read_at")),
            Str(d, "owner_id"),
            Guid.Parse(entity.GetProperty("id").GetString()!));
    }

    private static MessageRead MapMessage(JsonElement rec)
    {
        using var data = JsonDocument.Parse(rec.GetProperty("data").GetString()!);
        var d = data.RootElement;
        return new MessageRead(
            rec.GetProperty("id").GetInt64(),
            Str(d, "role") ?? "",
            Str(d, "content"),
            Str(d, "parts_json"),
            Str(d, "source"),
            Str(d, "discussion_id"),
            ParseUtc(Str(d, "timestamp")) ?? default);
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static DateTime? ParseUtc(string? value) =>
        value != null && DateTimeOffset.TryParse(value, out var t) ? t.UtcDateTime : null;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
