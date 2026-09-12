using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class HistoryPagingTests
{
    [Fact]
    public void CompositeCursorRoundTripsAndIsBoundToDiscussionSessionEpochAndEdge()
    {
        var boundary = new HistoryPageBoundary(
            DateTimeOffset.Parse("2026-09-12T12:00:00Z"), 42);
        var encoded = HistoryPageCursorCodec.Encode(new(
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Oldest,
            "compute-cursor",
            boundary,
            41));

        var decoded = HistoryPageCursorCodec.Decode(
            encoded,
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Oldest);

        Assert.Equal("compute-cursor", decoded.ComputeCursor);
        Assert.Equal(boundary, decoded.Boundary);
        Assert.Equal(41, decoded.OverlayAnchorId);
        Assert.Equal("history_cursor_mismatch", Assert.Throws<HistoryPageCursorException>(() =>
            HistoryPageCursorCodec.Decode(encoded, "discussion-2", "session-1", "epoch-1", HistoryPageCursorEdge.Oldest)).Code);
        Assert.Equal("history_cursor_stale", Assert.Throws<HistoryPageCursorException>(() =>
            HistoryPageCursorCodec.Decode(encoded, "discussion-1", "session-1", "epoch-2", HistoryPageCursorEdge.Oldest)).Code);
        Assert.Equal("invalid_cursor", Assert.Throws<HistoryPageCursorException>(() =>
            HistoryPageCursorCodec.Decode(encoded, "discussion-1", "session-1", "epoch-1", HistoryPageCursorEdge.Newest)).Code);
        Assert.Equal("invalid_cursor", Assert.Throws<HistoryPageCursorException>(() =>
            HistoryPageCursorCodec.Decode(new string('x', 4097), "discussion-1", "session-1", null, HistoryPageCursorEdge.Oldest)).Code);
    }

    [Fact]
    public void ExhaustedCanonicalEdgeBecomesOverlayOnlyWithoutRetainingInnerCursor()
    {
        var previous = new HistoryPageCursor(
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Oldest,
            "previous-compute",
            new HistoryPageBoundary(DateTimeOffset.Parse("2026-09-12T12:00:00Z"), 42),
            42);
        var exhausted = new SessionTranscriptPage
        {
            Page = new SessionTranscriptPageMetadata
            {
                Epoch = "epoch-1",
                OldestCursor = null,
                NewestCursor = null,
            },
        };

        Assert.Null(DiscussionEndpoints.ResolveComputeCursor(exhausted, previous, oldest: true));
        Assert.Equal("previous-compute", DiscussionEndpoints.ResolveComputeCursor(null, previous, oldest: true));
    }

    [Fact]
    public void TerminalAfterPageIsANullCursorNoOpButConsumedOverlayRowsAdvance()
    {
        Assert.False(DiscussionEndpoints.ShouldEmitHistoryCursor(
            "after",
            HistoryPageCursorEdge.Newest,
            pageHasRecords: false,
            hasEarlier: true,
            hasLater: false));
        Assert.True(DiscussionEndpoints.ShouldEmitHistoryCursor(
            "after",
            HistoryPageCursorEdge.Newest,
            pageHasRecords: true,
            hasEarlier: true,
            hasLater: false));
    }

    [Fact]
    public void EmptyComputeAfterWithOverlayAdvancePreservesCanonicalAnchor()
    {
        var previous = new HistoryPageCursor(
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Newest,
            "compute-after-500",
            new HistoryPageBoundary(DateTimeOffset.Parse("2026-09-12T12:00:00Z"), 500),
            500);
        var emptyComputeAfter = new SessionTranscriptPage
        {
            Page = new SessionTranscriptPageMetadata
            {
                Epoch = "epoch-1",
                Direction = "after",
                NewestCursor = null,
                HasEarlier = true,
                HasLater = false,
                BoundaryComplete = true,
            },
        };

        var preserved = DiscussionEndpoints.ResolveComputeCursor(
            emptyComputeAfter,
            previous,
            oldest: false,
            preservePreviousOnEmpty: true);
        var advancedOverlayCursor = HistoryPageCursorCodec.Encode(previous with
        {
            ComputeCursor = preserved,
            Boundary = new HistoryPageBoundary(DateTimeOffset.Parse("2026-09-12T12:01:00Z"), 501),
            OverlayAnchorId = 501,
        });
        var next = HistoryPageCursorCodec.Decode(
            advancedOverlayCursor,
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Newest);

        // When sequences 501..1000 arrive, the next Compute request remains
        // anchored after sequence 500 instead of falling back to newest.
        Assert.Equal("compute-after-500", next.ComputeCursor);
        Assert.Equal(501, next.OverlayAnchorId);
    }

    [Fact]
    public void FirstLargeCanonicalHistoryForOverlayOnlyCursorForcesNewestReset()
    {
        var overlayOnly = new HistoryPageCursor(
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Newest,
            ComputeCursor: null,
            new HistoryPageBoundary(DateTimeOffset.Parse("2026-09-12T12:00:00Z"), 20),
            OverlayAnchorId: 20);
        var newestCanonical = new SessionTranscriptPage
        {
            Messages = Enumerable.Range(501, 500)
                .Select(sequence => new SessionMessage
                {
                    Id = sequence,
                    SessionId = "session-1",
                    Role = "assistant",
                    EventType = "text",
                    MessageUid = $"uid-{sequence}",
                    Epoch = "epoch-1",
                    Sequence = sequence,
                })
                .ToList(),
            Page = new SessionTranscriptPageMetadata
            {
                Epoch = "epoch-1",
                Direction = "newest",
                HasEarlier = true,
                HasLater = false,
                BoundaryComplete = true,
            },
        };

        Assert.True(DiscussionEndpoints.RequiresCanonicalBackfillReset(
            "after", overlayOnly, newestCanonical));

        newestCanonical.Page.HasEarlier = false;
        Assert.False(DiscussionEndpoints.RequiresCanonicalBackfillReset(
            "after", overlayOnly, newestCanonical));
    }

    [Fact]
    public void CompositeAfterKeepsThousandOverlayRowsOnTheirOwnAnchor()
    {
        var cursor = new HistoryPageCursor(
            "discussion-1",
            "session-1",
            "epoch-1",
            HistoryPageCursorEdge.Newest,
            "compute-500",
            new HistoryPageBoundary(DateTimeOffset.Parse("2026-09-12T12:00:00Z"), 100),
            OverlayAnchorId: 100);
        Assert.Equal(100, DiscussionEndpoints.ResolveOverlayAfterAnchor(cursor));
        Assert.Equal(100, DiscussionEndpoints.ResolveOverlayAfterAnchor(
            cursor with { OverlayAnchorId = null }));
        var canonical = new[]
        {
            new SessionMessage
            {
                Id = 5_000,
                SessionId = "session-1",
                Role = "assistant",
                EventType = "text",
                Epoch = "epoch-1",
                Sequence = 500,
            },
        };

        var first = DiscussionEndpoints.ResolveNewestOverlayAnchor(
            "after",
            OverlayRows(101, 500, hasLater: true),
            cursor);
        Assert.Equal(600, first);

        cursor = cursor with { OverlayAnchorId = first };
        var second = DiscussionEndpoints.ResolveNewestOverlayAnchor(
            "after",
            OverlayRows(601, 500, hasLater: true),
            cursor);
        Assert.Equal(1_100, second);

        cursor = cursor with { OverlayAnchorId = second };
        var final = DiscussionEndpoints.ResolveNewestOverlayAnchor(
            "after",
            OverlayRows(1_101, 100, hasLater: false),
            cursor);
        Assert.Equal(1_200, final);
        Assert.NotEqual(canonical[^1].Id, final);
    }

    [Fact]
    public void ProjectionDoesNotRescueHistoricalUserBridgesAndUsesStableUids()
    {
        var records = new[]
        {
            Message(1, "user", "old bridge", "user-message", "user-1"),
            Message(2, "assistant", "mirrored", "nova-message", "assistant-1"),
            Message(3, "assistant", "durable card", "nova-message", "assistant-2"),
            Message(4, "system", "ambient", "event:automation", "event-1"),
            Message(5, "user", "queued", "queued-user-message", "user-2"),
        };

        var projected = DiscussionEndpoints.ProjectHistoryOverlays(
            records,
            new HashSet<string>(StringComparer.Ordinal) { "user-1" },
            new HashSet<string>(StringComparer.Ordinal) { "assistant-1" },
            sessionBacked: true,
            allowUserBridges: false);

        Assert.Equal(new[] { "assistant-2", "event-1" }, projected.Select(message => message.MessageUid));
        Assert.Equal(new[] { "nova-message", "event:automation" }, projected.Select(message => message.Source));

        var newest = DiscussionEndpoints.ProjectHistoryOverlays(
            records,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "assistant-1" },
            sessionBacked: true,
            allowUserBridges: true);
        Assert.Contains(newest, message => message.MessageUid == "user-1" && message.Source == "user-message");
        Assert.DoesNotContain(newest, message => message.Source == "queued-user-message");
    }

    [Fact]
    public async Task AlignedOverlayRangeWalksPastKernelThousandRecordPageWithoutGaps()
    {
        var start = DateTimeOffset.Parse("2026-09-12T10:00:00Z");
        var messages = Enumerable.Range(1, 1_505)
            .Select(index => Message(
                index,
                "system",
                $"event-{index}",
                "event:test",
                $"event-{index}",
                start.AddMilliseconds(index)))
            .ToArray();
        var service = new InMemoryDiscussions(messages);

        var page = await DiscussionEndpoints.ReadCompleteOverlayIntervalAsync(
            service,
            DiscussionId,
            new DiscussionMessagePageBoundary(start, 0),
            null,
            CancellationToken.None);

        Assert.Equal(1_505, page.Count);
        Assert.Equal(Enumerable.Range(1, 1_505).Select(value => (long)value), page.Select(message => message.Id));
        Assert.Equal(2, service.PageCalls);
    }

    private static readonly Guid DiscussionId = Guid.Parse("41f1726a-c8d3-4aec-983f-36c83c418c2a");

    private static DiscussionMessage Message(
        long id,
        string role,
        string content,
        string source,
        string uid,
        DateTimeOffset? createdAt = null)
        => new(
            id,
            DiscussionId,
            role,
            content,
            new JsonObject
            {
                ["source"] = source,
                ["uid"] = uid,
            },
            createdAt ?? DateTimeOffset.Parse("2026-09-12T12:00:00Z").AddMilliseconds(id));

    private static OverlayPageRead OverlayRows(int firstId, int count, bool hasLater)
    {
        var messages = Enumerable.Range(firstId, count)
            .Select(index => Message(
                index,
                "system",
                $"event-{index}",
                "event:test",
                $"event-{index}"))
            .ToArray();
        return new OverlayPageRead(
            messages,
            HasEarlier: firstId > 1,
            hasLater,
            messages[0].Id,
            messages[^1].Id);
    }

    private sealed class InMemoryDiscussions(IReadOnlyList<DiscussionMessage> messages) : IDiscussions
    {
        public int PageCalls { get; private set; }

        public Task<DiscussionMessagePage> GetMessagePageAsync(
            Guid discussionId,
            DiscussionMessagePageRequest request,
            CancellationToken ct = default)
        {
            PageCalls++;
            IEnumerable<DiscussionMessage> bounded = messages.Where(message => message.DiscussionId == discussionId);
            if (request.FromInclusive is { } from)
                bounded = bounded.Where(message => Compare(message, from) >= 0);
            if (request.ToExclusive is { } to)
                bounded = bounded.Where(message => Compare(message, to) < 0);
            if (request.BeforeId is { } beforeId)
                bounded = bounded.Where(message => message.Id < beforeId);
            if (request.AfterId is { } afterId)
                bounded = bounded.Where(message => message.Id > afterId);

            var all = bounded.OrderBy(message => message.Id).ToArray();
            var limit = Math.Clamp(request.Limit, 1, 1000);
            var selected = request.AfterId.HasValue
                ? all.Take(limit).ToArray()
                : all.TakeLast(limit).ToArray();
            var hasEarlier = selected.Length > 0 && all.Any(message => message.Id < selected[0].Id);
            var hasLater = selected.Length > 0 && all.Any(message => message.Id > selected[^1].Id);
            return Task.FromResult(new DiscussionMessagePage(
                selected,
                hasEarlier,
                hasLater,
                selected.FirstOrDefault()?.Id,
                selected.LastOrDefault()?.Id));
        }

        private static int Compare(DiscussionMessage message, DiscussionMessagePageBoundary boundary)
        {
            var timestamp = message.CreatedAt.CompareTo(boundary.CreatedAt);
            return timestamp != 0 ? timestamp : message.Id.CompareTo(boundary.Id);
        }

        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
