using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// The context an agent received ahead of one user message, kept on that message's record so
/// Message Info can show exactly what was injected. One record key holds the
/// <see cref="NovaContextBuilder.BuildMetadata"/> fields and the exact text in
/// <see cref="BlockKey"/>; history returns the same object as the message's metadata.
/// </summary>
public static class NovaMessageContext
{
    /// <summary>The user-message record key.</summary>
    public const string RecordKey = "nova_context";

    /// <summary>The exact injected text: the &lt;nova-context&gt; block, then the prior-message block if any.</summary>
    public const string BlockKey = "novaContextBlock";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject ToRecord(IReadOnlyDictionary<string, object?> metadata, string injected)
    {
        var record = JsonSerializer.SerializeToNode(metadata, JsonOptions) as JsonObject ?? [];
        record[BlockKey] = injected;
        return record;
    }

    /// <summary>The stored context of one record as wire metadata, or null for a record that has none.</summary>
    public static JsonObject? Read(DiscussionMessage? record)
        => record?.Metadata[RecordKey] is JsonObject context ? (JsonObject)context.DeepClone() : null;

    /// <summary>
    /// The stored contexts among <paramref name="records"/>, one per accepted user message uid
    /// (its latest record wins, like the user bridge it belongs to), oldest first.
    /// </summary>
    public static IReadOnlyList<MessageContextDto> ForUserMessages(IEnumerable<DiscussionMessage> records)
        => records
            .Where(m => m.Role == "user"
                && m.Metadata["source"]?.GetValue<string>() is "user-message" or "queued-user-message"
                && !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>())
                && m.Metadata[RecordKey] is JsonObject)
            .GroupBy(m => m.Metadata["uid"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(g => g.OrderBy(m => m.Id).Last())
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new MessageContextDto(m.Metadata["uid"]!.GetValue<string>(), Read(m)!))
            .ToList();
}

/// <summary>One user message's stored context on a history page, addressed by its stable uid.</summary>
public sealed record MessageContextDto(string MessageUid, JsonObject Metadata);
