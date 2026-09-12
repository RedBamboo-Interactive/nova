using System.Text;
using System.Text.Json;

namespace Leaf.Plugins.Nova;

internal enum HistoryPageCursorEdge
{
    Oldest,
    Newest,
}

internal sealed record HistoryPageBoundary(DateTimeOffset Timestamp, long Id);

internal sealed record HistoryPageCursor(
    string DiscussionId,
    string? SessionId,
    string? Epoch,
    HistoryPageCursorEdge Edge,
    string? ComputeCursor,
    HistoryPageBoundary? Boundary,
    long? OverlayAnchorId);

internal sealed class HistoryPageCursorException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Nova cursors coordinate RedCompute's transcript keyset with the discussion
/// overlay keyset. The payload is deliberately opaque to browsers and bound to
/// one authorized discussion/session/epoch before any storage anchor is used.
/// </summary>
internal static class HistoryPageCursorCodec
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode(HistoryPageCursor cursor)
    {
        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            DiscussionId = cursor.DiscussionId,
            SessionId = cursor.SessionId,
            Epoch = cursor.Epoch,
            Edge = cursor.Edge == HistoryPageCursorEdge.Oldest ? "oldest" : "newest",
            ComputeCursor = cursor.ComputeCursor,
            BoundaryTimestamp = cursor.Boundary?.Timestamp.ToString("O"),
            BoundaryId = cursor.Boundary?.Id,
            OverlayAnchorId = cursor.OverlayAnchorId,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static HistoryPageCursor Decode(
        string encoded,
        string expectedDiscussionId,
        string? expectedSessionId,
        string? expectedEpoch,
        HistoryPageCursorEdge expectedEdge)
    {
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 4096)
            throw Invalid();

        CursorPayload payload;
        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            payload = JsonSerializer.Deserialize<CursorPayload>(
                Convert.FromBase64String(normalized), JsonOptions) ?? throw Invalid();
        }
        catch (HistoryPageCursorException)
        {
            throw;
        }
        catch
        {
            throw Invalid();
        }

        var expectedEdgeValue = expectedEdge == HistoryPageCursorEdge.Oldest ? "oldest" : "newest";
        if (payload.Version != CurrentVersion
            || string.IsNullOrWhiteSpace(payload.DiscussionId)
            || !string.Equals(payload.Edge, expectedEdgeValue, StringComparison.Ordinal))
            throw Invalid();

        if (!string.Equals(payload.DiscussionId, expectedDiscussionId, StringComparison.Ordinal)
            || !string.Equals(payload.SessionId, expectedSessionId, StringComparison.Ordinal))
            throw new HistoryPageCursorException(
                "history_cursor_mismatch",
                "The history cursor belongs to another discussion or session");

        if (expectedEpoch is not null
            && !string.Equals(payload.Epoch, expectedEpoch, StringComparison.Ordinal))
            throw new HistoryPageCursorException(
                "history_cursor_stale",
                "The history cursor belongs to an earlier transcript epoch");

        HistoryPageBoundary? boundary = null;
        if (payload.BoundaryTimestamp is not null || payload.BoundaryId.HasValue)
        {
            if (!payload.BoundaryId.HasValue
                || !DateTimeOffset.TryParse(payload.BoundaryTimestamp, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                throw Invalid();
            boundary = new HistoryPageBoundary(timestamp.ToUniversalTime(), payload.BoundaryId.Value);
        }

        return new HistoryPageCursor(
            payload.DiscussionId,
            payload.SessionId,
            payload.Epoch,
            expectedEdge,
            payload.ComputeCursor,
            boundary,
            payload.OverlayAnchorId);
    }

    private static HistoryPageCursorException Invalid() => new(
        "invalid_cursor",
        "The history cursor is malformed or unsupported");

    private sealed class CursorPayload
    {
        public int Version { get; set; }
        public string? DiscussionId { get; set; }
        public string? SessionId { get; set; }
        public string? Epoch { get; set; }
        public string? Edge { get; set; }
        public string? ComputeCursor { get; set; }
        public string? BoundaryTimestamp { get; set; }
        public long? BoundaryId { get; set; }
        public long? OverlayAnchorId { get; set; }
    }
}
