using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

public sealed record DelegationSessionActivity(
    string SessionId, string? Title, string? RepositoryId, string? Repository,
    string Status, bool Available, string? LastKnownStatus = null, string? BlockedReason = null, string? ErrorCode = null,
    string? Branch = null, string? WorktreePath = null);

public sealed record DiscussionDelegationActivity(
    int OngoingCount, IReadOnlyList<DelegationSessionActivity> Sessions, bool Available,
    int UnknownCount, IReadOnlyList<string> LinkedSessionIds);

internal sealed record DelegationLink(
    string SessionId, long MarkerId, string? RepositoryId, string? Repository,
    string? Branch = null, string? WorktreePath = null);

/// <summary>
/// Product-only read projection. Durable system markers establish membership;
/// authorized Compute session/queue snapshots establish activity. Neither callbacks
/// nor this projection write provider state, discussion status, or transcript records.
/// </summary>
public sealed class DelegationActivity(IDiscussions messages, RedComputeClient compute, IEntityStore entities)
{
    private sealed class MarkerIndex
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<string, DelegationLink> Links = new(StringComparer.Ordinal);
        public long Cursor;
        public int MessageCount = -1;
        public DateTime LastActivity;
    }

    private readonly ConcurrentDictionary<Guid, MarkerIndex> indexes = new();
    // Only retain a lifecycle fact, never private session titles or authorization.
    // A continuation has a new marker key, so an old observation cannot settle it.
    private readonly ConcurrentDictionary<(string Scope, Guid Discussion, long Marker), string> lastStatus = new();
    private sealed class SnapshotCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public RedComputeClient.DelegationSnapshotResult? Result;
        public DateTimeOffset FetchedAt;
        public DateTimeOffset ExpiresAt;
        public long MarkerId;
    }
    private readonly ConcurrentDictionary<(string Scope, string Session), SnapshotCache> sessionCache = new();
    // Markers whose session was last observed finished (a lifecycle fact, keyed by marker so a
    // continuation's new marker re-opens the link). A settled link is not read again until the
    // request names its session, asks for a fresh read, or a newer marker supersedes it. What it
    // contributes to every caller's answer is the same: nothing beyond its id in LinkedSessionIds.
    private readonly ConcurrentDictionary<(Guid Discussion, long Marker), byte> settled = new();
    // Session id -> ai-session entity id. Only the location is cached; ownership, Agent and
    // confidentiality are read from that entity again on every request.
    private readonly ConcurrentDictionary<string, Guid> metadataEntities = new(StringComparer.Ordinal);
    internal const int MaxCachedMetadataEntities = 4096;
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<IReadOnlyDictionary<string, DiscussionDelegationActivity>> ReadAsync(
        IEnumerable<DiscussionRead> discussions, HttpContext context, CancellationToken ct = default,
        IReadOnlySet<string>? changedSessionIds = null, bool fresh = false)
    {
        // Use the direct owner/owning-Agent policy even for non-confidential parents.
        // Ambient local access and another Agent's authority are not delegation authority.
        var authorized = discussions.Where(d => !DiscussionStatus.IsClosed(d.Status)
            && DiscussionAccessPolicy.CanRead(d with { Confidential = true }, context)).ToArray();
        var linked = new Dictionary<string, IReadOnlyList<DelegationLink>>();
        foreach (var discussion in authorized)
            linked[discussion.Id] = await ReadLinksAsync(discussion, ct);

        // A link last observed finished stays settled unless this request names its session
        // (a lifecycle frame for it arrived), asks for a fresh read, or a continuation added a
        // newer marker (a different key). Only the links still needed are read below.
        // Decided once per request: a concurrent request may settle or re-open a link meanwhile.
        var skipped = authorized.SelectMany(discussion => linked[discussion.Id]
                .Where(link => !fresh && changedSessionIds?.Contains(link.SessionId) != true
                    && settled.ContainsKey((discussion.EntityId, link.MarkerId)))
                .Select(link => (discussion.EntityId, link.MarkerId)))
            .ToHashSet();
        bool IsSettled(DiscussionRead discussion, DelegationLink link) => skipped.Contains((discussion.EntityId, link.MarkerId));
        var needed = authorized.SelectMany(discussion => linked[discussion.Id]
                .Where(link => !IsSettled(discussion, link)).Select(link => link.SessionId))
            .ToHashSet(StringComparer.Ordinal);

        // Recheck canonical ownership/confidentiality on EVERY read. Freshness may
        // cache queue observations, never grant continued access to a child's title.
        // One entity read per needed session rather than N HTTP probes.
        var metadata = await ReadMetadataAsync(needed, ct);

        // One bounded read per distinct linked session, not a global Code-session list
        // or one fanout for every discussion × session pair. No background watcher.
        using var concurrency = new SemaphoreSlim(4);
        var snapshots = new ConcurrentDictionary<string, RedComputeClient.DelegationSnapshotResult>();
        var scope = ScopeKey(context);
        await Task.WhenAll(linked.Values.SelectMany(links => links).GroupBy(link => link.SessionId, StringComparer.Ordinal)
            .Where(group => needed.Contains(group.Key))
            .Select(async group =>
            {
                if (metadata is null) { snapshots[group.Key] = new(null); return; }
                if (!metadata.TryGetValue(group.Key, out var current)) { snapshots[group.Key] = new(null, true); return; }
                await concurrency.WaitAsync(ct);
                try
                {
                    var snapshot = await ReadSnapshotAsync(scope, group.Key, group.Max(link => link.MarkerId),
                        fresh || changedSessionIds?.Contains(group.Key) == true, ct);
                    snapshots[group.Key] = snapshot.Value is { } state ? snapshot with { Value = state with {
                        OwnerId = current.OwnerId, AgentId = current.AgentId, Confidential = current.Confidential,
                        Title = current.Title, RepositoryId = current.RepositoryId,
                    } } : snapshot;
                }
                finally { concurrency.Release(); }
            }));

        var result = new Dictionary<string, DiscussionDelegationActivity>();
        foreach (var discussion in authorized)
        {
            var ongoing = new List<DelegationSessionActivity>();
            var available = true;
            var ongoingCount = 0;
            var unknownCount = 0;
            foreach (var link in linked[discussion.Id])
            {
                // Settled: finished, so neither ongoing nor unknown, exactly as when it was read.
                if (IsSettled(discussion, link)) continue;
                var snapshot = snapshots[link.SessionId];
                var key = (scope, discussion.EntityId, link.MarkerId);
                if (snapshot.Denied)
                {
                    lastStatus[key] = "finished";
                    continue;
                }
                if (snapshot.Value is not { } state)
                {
                    available = false;
                    // Never infer completion from an outage. A previously settled
                    // marker stays settled; an unresolved accepted link stays unknown.
                    AddUnavailable();
                    continue;
                }
                if (!MatchesScope(discussion, state))
                {
                    lastStatus[key] = "finished";
                    continue;
                }
                var status = ProjectStatus(state);
                if (status == "unavailable")
                {
                    available = false;
                    AddUnavailable();
                    continue;
                }
                var isOngoing = IsOutstanding(status);
                lastStatus[key] = status;
                // Only an observed terminal state settles a link. A denial, a scope mismatch or
                // an outage leaves it to be read again.
                if (status == "finished") settled[(discussion.EntityId, link.MarkerId)] = 0;
                else settled.TryRemove((discussion.EntityId, link.MarkerId), out _);
                if (isOngoing)
                {
                    ongoingCount++;
                    ongoing.Add(new(link.SessionId, state.Title,
                        state.RepositoryId ?? link.RepositoryId, link.Repository, status, true,
                        BlockedReason: state.QueueBlockedReason, ErrorCode: state.QueueErrorCode,
                        Branch: link.Branch, WorktreePath: link.WorktreePath));
                }

                void AddUnavailable()
                {
                    lastStatus.TryGetValue(key, out var prior);
                    if (prior == "finished") return;
                    if (IsOutstanding(prior)) ongoingCount++;
                    else unknownCount++;
                    ongoing.Add(new(link.SessionId, null, link.RepositoryId, link.Repository, "unavailable", false, prior,
                        Branch: link.Branch, WorktreePath: link.WorktreePath));
                }
            }
            result[discussion.Id] = new(ongoingCount, ongoing, available, unknownCount,
                linked[discussion.Id].Select(link => link.SessionId).ToArray());
        }
        return result;
    }

    private async Task<Dictionary<string, RedComputeClient.DelegationSnapshot>?> ReadMetadataAsync(
        IReadOnlySet<string> linked, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, RedComputeClient.DelegationSnapshot>(StringComparer.Ordinal);
        if (linked.Count == 0) return new(StringComparer.Ordinal);
        try
        {
            using var concurrency = new SemaphoreSlim(4);
            await Task.WhenAll(linked.Select(async sessionId =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    if (await ReadSessionEntityAsync(sessionId, ct) is not { } entity) return;
                    var data = entity.Data;
                    // This metadata read authorizes/display-labels the child;
                    // lifecycle comes exclusively from the Compute observation.
                    result[sessionId] = new("Unknown", null, entity.Name,
                        data["user_id"]?.ToString(), data["owner_agent_id"]?.ToString(),
                        data["confidential"]?.ToString() == "true", data["repository"]?.ToString(), null, null, null);
                }
                finally { concurrency.Release(); }
            }));
            return new(result, StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// The session's ai-session entity, read fresh. Finding it by <c>data.session_id</c> happens
    /// once per session and is remembered (bounded); the entity itself is read by id on every
    /// request, so a changed owner, Agent or confidentiality applies to the next one.
    /// </summary>
    private async Task<LeafEntity?> ReadSessionEntityAsync(string sessionId, CancellationToken ct)
    {
        if (metadataEntities.TryGetValue(sessionId, out var entityId))
        {
            var known = await entities.GetAsync(entityId, ct);
            if (known is not null && known.TypeSlug == "ai-session" && known.Data["session_id"]?.ToString() == sessionId)
                return known;
            metadataEntities.TryRemove(sessionId, out _);
        }
        var page = await entities.QueryAsync(new EntityQuery {
            TypeSlug = "ai-session", Limit = 10, OrderById = true,
            DataEquals = new Dictionary<string, object?> { ["session_id"] = sessionId },
        }, ct);
        var entity = page.LastOrDefault(candidate => candidate.TypeSlug == "ai-session"
            && candidate.Data["session_id"]?.ToString() == sessionId);
        // Not written yet, or gone: nothing is remembered, so the next request looks again.
        if (entity is null) return null;
        if (metadataEntities.Count >= MaxCachedMetadataEntities) metadataEntities.Clear();
        metadataEntities[sessionId] = entity.Id;
        return entity;
    }

    private async Task<RedComputeClient.DelegationSnapshotResult> ReadSnapshotAsync(
        string scope, string sessionId, long markerId, bool force, CancellationToken ct)
    {
        var requestedAt = Now();
        var cache = sessionCache.GetOrAdd((scope, sessionId), _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            // Scope includes verified claims (hashed, never tokens). New admissions
            // and exact session invalidations bypass terminal freshness; concurrent
            // requests that arrived before this read finishes reuse its observation.
            if (cache.Result is not null && cache.ExpiresAt > Now() && cache.MarkerId >= markerId
                && (!force || cache.FetchedAt >= requestedAt)) return cache.Result;
            cache.Result = await compute.GetDelegationSnapshotAsync(sessionId, ct);
            cache.MarkerId = markerId;
            cache.FetchedAt = Now();
            var terminal = cache.Result.Denied || cache.Result.Value is { } state && ProjectStatus(state) == "finished";
            cache.ExpiresAt = cache.FetchedAt.AddSeconds(terminal ? 30 : 2);
            return cache.Result;
        }
        finally { cache.Gate.Release(); }
    }

    private static string ScopeKey(HttpContext context)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            authentication = context.User.Identity?.AuthenticationType,
            claims = context.User.Claims.OrderBy(claim => claim.Type).ThenBy(claim => claim.Value)
                .Select(claim => new { claim.Type, claim.Value }),
        }))));

    internal static bool MatchesScope(DiscussionRead discussion, RedComputeClient.DelegationSnapshot session)
        => string.Equals(session.OwnerId, discussion.OwnerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(session.AgentId, discussion.AgentId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(session.OwnerId)
            && !string.IsNullOrWhiteSpace(session.AgentId)
            && (!session.Confidential || discussion.Confidential);

    private static bool IsOutstanding(string? status)
        => status is "running" or "queued" or "starting" or "waiting_to_resume" or "blocked" or "failed";

    internal static string ProjectStatus(RedComputeClient.DelegationSnapshot session)
    {
        // Explicit stop/failure is terminal. Maintenance recovery with accepted
        // pending input remains work, even though the provider itself is stopped.
        if (session.Status == "Ended"
            || session.Status == "Stopped" && !DiscussionStatus.IsAutoResumableStop(session.StopReason))
            return "finished";
        if (session.Status == "Active" || session.QueueBlockedReason == "active_turn") return "running";
        if (session.Status == "Starting" || session.QueueBlockedReason == "session_starting") return "starting";
        if (session.QueueDepth is null || session.QueueState is not ("empty" or "ready" or "delivering" or "waiting_for_session" or "failed"))
            return "unavailable";
        // A durable failed head remains actionable work, not a running turn or
        // an invented completion. Preserve its count and link for inspection.
        if (session.QueueDepth > 0 && (session.QueueState == "failed" || session.Status == "Error"
            || session.QueueErrorCode == "deployment_target_failed")) return "failed";
        if (session.QueueDepth > 0)
        {
            if (session.QueueBlockedReason == "user_stopped") return "blocked";
            if (session.QueueErrorCode is "deployment_target_pending" or "deployment_target_unavailable") return "queued";
            if (session.QueueErrorCode is not null && session.QueueErrorCode != "recovery_revalidate") return "blocked";
            if (session.Status == "Stopped") return "waiting_to_resume";
            return "queued";
        }
        if (session.Status == "Error" || session.QueueState == "failed") return "finished";
        // The turn ended but its background work still runs and will wake the session again.
        if (session.WorkState == "background") return "running";
        return session.Status is "Idle" or "Stopped" && session.QueueState == "empty" ? "finished" : "unavailable";
    }

    private async Task<IReadOnlyList<DelegationLink>> ReadLinksAsync(DiscussionRead discussion, CancellationToken ct)
    {
        var index = indexes.GetOrAdd(discussion.EntityId, _ => new());
        await index.Gate.WaitAsync(ct);
        try
        {
            if (discussion.MessageCount < index.MessageCount)
            {
                index.Cursor = 0;
                index.Links.Clear();
            }
            if (index.MessageCount == discussion.MessageCount && index.LastActivity == discussion.LastActivity)
                return index.Links.Values.ToArray();
            // One cold index of legacy markers, followed only by new record-id pages.
            // Ordinary status ticks with unchanged history never scan the transcript.
            while (true)
            {
                var page = await messages.GetMessagesAsync(discussion.EntityId, 1000, index.Cursor, ct);
                foreach (var message in page)
                {
                    var link = ParseLink(message);
                    if (link is null) continue;
                    if (index.Links.TryGetValue(link.SessionId, out var previous))
                        link = link with { RepositoryId = link.RepositoryId ?? previous.RepositoryId,
                            Repository = link.Repository ?? previous.Repository,
                            Branch = link.Branch ?? previous.Branch,
                            WorktreePath = link.WorktreePath ?? previous.WorktreePath };
                    index.Links[link.SessionId] = link;
                }
                if (page.Count == 0) break;
                var next = page.Max(message => message.Id);
                if (next <= index.Cursor) throw new InvalidOperationException("Delegation marker cursor did not advance");
                index.Cursor = next;
                if (page.Count < 1000) break;
            }
            index.MessageCount = discussion.MessageCount;
            index.LastActivity = discussion.LastActivity;
            return index.Links.Values.ToArray();
        }
        finally { index.Gate.Release(); }
    }

    internal static DelegationLink? ParseLink(DiscussionMessage message)
    {
        if (message.Role != "system" || message.Metadata["source"]?.ToString() != "event:delegation") return null;
        var parts = message.Metadata["parts_json"]?.ToString();
        if (parts is null) return null;
        try
        {
            using var document = JsonDocument.Parse(parts);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (String(part, "type") != "event_data" || !part.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object || String(data, "status") != "started") continue;
                var sessionId = String(data, "sessionId");
                if (!ValidSessionId(sessionId)) continue;
                return new(sessionId!, message.Id, String(data, "repositoryId"), String(data, "repository"),
                    String(data, "branch"), String(data, "worktreePath"));
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal static bool ValidSessionId(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 200
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static string? String(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
}
