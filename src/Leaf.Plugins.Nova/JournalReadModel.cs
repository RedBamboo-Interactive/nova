using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>A journal read asked for something that can't be answered, with the code the route returns.</summary>
public sealed class JournalRequestException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// One Agent's day, computed on read from discussions, transcripts and jobs. It writes nothing: no
/// table, no summary, no transcript record, no read marker. Confidential discussions and jobs are
/// left out entirely, from entries, counts and edges, which is the Live activity rule; what remains
/// is filtered through <see cref="DiscussionAccessPolicy"/> on every read, however much is cached.
/// </summary>
public sealed class JournalReadModel(
    DiscussionStore store,
    IDiscussions messages,
    RedComputeClient compute,
    IEntityStore entities,
    IAgentWorkspacePathResolver workspaces,
    IJournalPeers peers,
    Func<string?> novaAgentId,
    TimeProvider? time = null)
{
    private const int JobsPerPage = 500;
    private const int MaxJobPages = 60;
    private const int TranscriptPageSize = 500;
    private const int MaxTranscriptPages = 400;
    private const int MaxArtifacts = 24;
    private const int ArtifactPages = 3;
    private const int MaxNamedRepositories = 5;
    private const int MaxCommitRepositories = 8;
    private const int MaxCacheEntries = 4000;
    // Each holds a whole day, so far fewer of them than of the small facts.
    private const int MaxCachedResponses = 64;
    private const int MaxQuotePages = 10;

    private static readonly TimeSpan TodayFreshness = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClosedDayFreshness = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClosedDayJobsFreshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OpenMonthFreshness = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClosedMonthFreshness = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PeerFreshness = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CommitFreshness = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionFreshness = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommitLookback = TimeSpan.FromDays(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Every read of RedCompute takes a slot, so one day's worth of threads can't flood it.
    private readonly SemaphoreSlim _computeSlots = new(4);
    private readonly SemaphoreSlim _codeSlots = new(4);
    private readonly SemaphoreSlim _overlaySlots = new(8);

    // A first record never changes, so a discussion's cause lives as long as the process.
    private readonly ConcurrentDictionary<Guid, JournalCauseFacts> _causes = new();
    // The calendar's cheaper reading from the overlay alone; a day view never trusts it.
    private readonly ConcurrentDictionary<Guid, string> _overlayCauses = new();
    // Reads made with the caller's own identity are kept per caller: what one may read says nothing
    // about another. Keyed too by everything that moves when a discussion gains a record; a running
    // one is never kept.
    private readonly ConcurrentDictionary<(string Subject, Guid Entity, DateOnly Day, string Version), DayFacts> _facts = new();
    private readonly ConcurrentDictionary<(string Subject, Guid Job), string?> _jobTexts = new();
    private readonly ConcurrentDictionary<(string DiscussionId, string Uid), JournalRequest?> _quotes = new();
    private readonly ConcurrentDictionary<string, Stamped<object?>> _responses = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _responseGates = new();
    private readonly ConcurrentDictionary<string, Stamped<JobsRead>> _jobs = new();
    private readonly ConcurrentDictionary<string, Stamped<object?>> _peerCache = new();
    private readonly ConcurrentDictionary<string, Stamped<RedComputeClient.DelegationSnapshotResult>> _sessions = new();

    private sealed record Stamped<T>(T Value, DateTimeOffset ReadAt);

    /// <summary>What one discussion did inside one day, from its transcript and its overlay records.</summary>
    private sealed record DayFacts(
        bool Active, DateTimeOffset? FirstAt, string? LastText, string? LastTextUid, int Messages,
        IReadOnlyList<JournalTurn> Turns, IReadOnlyList<JournalNote> NotesIn,
        IReadOnlyList<(string? Uid, DateTimeOffset At)> UnstampedHeartbeatNotes,
        IReadOnlyList<DelegationLink> Links, IReadOnlyList<string> AssistantTexts,
        IReadOnlyList<JournalArtifact> Artifacts, IReadOnlySet<string> Unavailable,
        bool WalkedToStart = false, SessionMessage? CanonicalFirst = null, bool TranscriptDenied = false);

    private sealed record JobsRead(IReadOnlyList<JournalJob> Items, string? Unavailable);

    public sealed record JournalContextRef(string Kind, string? Id, string? EntityId);

    public sealed record JournalJob(
        Guid Id, string? Name, string? Capability, string Status, DateTimeOffset QueuedAt, Guid? ParentJobId,
        bool Confidential, string? ErrorMessage, string? ActorKind, string? ActorEntityId,
        IReadOnlyList<JournalContextRef> Context)
    {
        public bool HasFailed => Status is "Failed" or "TimedOut";
        public bool IsTerminal => Status is not ("Queued" or "Running");

        public string? ContextId(string kind)
            => Context.FirstOrDefault(item => item.Kind == kind) is { } found ? found.EntityId ?? found.Id : null;

        /// <summary>Whose job this is, through its actor where it has one, else the Agent it names.</summary>
        public string? AgentRef
            => ActorKind == "agent" && ActorEntityId is not null
                ? ActorEntityId
                : Context.FirstOrDefault(item => item.Kind == "agent")?.EntityId;
    }

    public async Task<JournalDayResponse> ReadDayAsync(
        string? agentRef, string? day, HttpContext context, CancellationToken ct = default)
    {
        var agent = await ResolveAgentAsync(agentRef, ct);
        var zone = await ResolveZoneAsync(agent, ct);
        var today = JournalWindow.DayOf(_time.GetUtcNow(), zone);
        var requested = today;
        if (!string.IsNullOrWhiteSpace(day)
            && !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out requested))
            throw new JournalRequestException(400, "invalid_day", "day must be a date written YYYY-MM-DD");
        if (requested > today)
            throw new JournalRequestException(400, "future_day", "That day hasn't happened yet");

        var key = $"day|{Subject(context)}|{agent.Id}|{requested:yyyy-MM-dd}";
        var freshness = requested == today ? TodayFreshness : ClosedDayFreshness;
        return (JournalDayResponse)(await CachedAsync(key, freshness,
            async () => await BuildDayAsync(agent, requested, today, zone, context, ct), ct))!;
    }

    public async Task<JournalMonthResponse> ReadMonthAsync(
        string? agentRef, string? month, HttpContext context, CancellationToken ct = default)
    {
        var agent = await ResolveAgentAsync(agentRef, ct);
        var zone = await ResolveZoneAsync(agent, ct);
        var today = JournalWindow.DayOf(_time.GetUtcNow(), zone);
        var first = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(month)
            && !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
            throw new JournalRequestException(400, "invalid_month", "month must be written YYYY-MM");

        var key = $"month|{Subject(context)}|{agent.Id}|{first:yyyy-MM}";
        var open = first.AddMonths(1) > today;
        return (JournalMonthResponse)(await CachedAsync(key, open ? OpenMonthFreshness : ClosedMonthFreshness,
            async () => await BuildMonthAsync(agent, first, today, zone, context, ct), ct))!;
    }

    private async Task<object?> CachedAsync(string key, TimeSpan freshness, Func<Task<object?>> build, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_responses.TryGetValue(key, out var hit) && now - hit.ReadAt < freshness) return hit.Value;

        // Concurrent readers of one day share its read rather than each walking the transcripts.
        var gate = _responseGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            now = _time.GetUtcNow();
            if (_responses.TryGetValue(key, out hit) && now - hit.ReadAt < freshness) return hit.Value;
            var value = await build();
            Bound(_responses, MaxCachedResponses);
            _responses[key] = new(value, _time.GetUtcNow());
            return value;
        }
        finally { gate.Release(); }
    }

    private static void Bound<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, int limit = MaxCacheEntries) where TKey : notnull
    {
        if (cache.Count >= limit) cache.Clear();
    }

    internal int CachedResponseCount => _responses.Count;

    private static string Subject(HttpContext context) => context.User.FindFirstValue("sub") ?? "";

    private async Task<LeafEntity> ResolveAgentAsync(string? agentRef, CancellationToken ct)
    {
        var reference = string.IsNullOrWhiteSpace(agentRef) ? novaAgentId() : agentRef.Trim();
        if (string.IsNullOrWhiteSpace(reference))
            throw new JournalRequestException(404, "agent_not_found", "No Agent to read a journal for");
        var agent = Guid.TryParse(reference, out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync("agent", reference, ct);
        return agent is { TypeSlug: "agent" }
            ? agent
            : throw new JournalRequestException(404, "agent_not_found", "No such Agent");
    }

    /// <summary>
    /// The zone is the one the Agent's Heartbeat automation is scheduled in, the setting that already
    /// decides when its day runs; an Agent without one follows the machine.
    /// </summary>
    private async Task<TimeZoneInfo> ResolveZoneAsync(LeafEntity agent, CancellationToken ct)
    {
        if (HeartbeatService.AutomationId(agent.Data) is { } automationId)
        {
            try
            {
                var automation = await entities.GetAsync(automationId, ct);
                if (automation?.Data["trigger"]?["timezone"] is JsonValue value
                    && value.TryGetValue<string>(out var zoneId) && !string.IsNullOrWhiteSpace(zoneId))
                    return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return _time.LocalTimeZone;
    }

    private async Task<List<DiscussionRead>> ReadableAsync(LeafEntity agent, HttpContext context, CancellationToken ct)
    {
        var agentId = agent.Id.ToString();
        var isNova = string.Equals(agentId, novaAgentId(), StringComparison.OrdinalIgnoreCase);
        return (await store.ListAsync(agentId, ct))
            .Where(discussion => discussion.Type is "chat" or "live" or HeartbeatService.DiscussionType)
            // Only Nova's own discussions may predate the Agent field.
            .Where(discussion => discussion.AgentId == agentId || (discussion.AgentId is null && isNova))
            .Where(discussion => !discussion.Confidential && DiscussionAccessPolicy.CanRead(discussion, context))
            .ToList();
    }

    private static DateTimeOffset Utc(DateTime value)
        => new(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());

    private async Task<JournalDayResponse> BuildDayAsync(
        LeafEntity agent, DateOnly day, DateOnly today, TimeZoneInfo zone, HttpContext context, CancellationToken ct)
    {
        var window = JournalWindow.For(day, zone);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var agentId = agent.Id.ToString();

        var readable = await ReadableAsync(agent, context, ct);
        var byId = readable.ToDictionary(discussion => discussion.Id, StringComparer.Ordinal);

        // Candidates by the listing; the transcript confirms. A running discussion may not have
        // bumped its activity yet, so it stays a candidate until it says otherwise.
        var candidates = readable.Where(discussion => Utc(discussion.CreatedAt) < window.End
            && (Utc(discussion.LastActivity) >= window.Start || (day == today && discussion.Status == "thinking"))).ToList();

        var facts = new Dictionary<string, DayFacts>(StringComparer.Ordinal);
        await Task.WhenAll(candidates.Select(async discussion =>
        {
            var read = await ReadFactsAsync(discussion, window, context, ct);
            lock (facts) facts[discussion.Id] = read;
        }));
        foreach (var read in facts.Values) unavailable.UnionWith(read.Unavailable);

        // A person who can read nothing of this Agent's sees nothing of what it ran either.
        var mayReadAgent = readable.Count > 0;
        var jobs = mayReadAgent ? await ReadJobsAsync(window, day == today, context, ct) : new JobsRead([], null);
        if (jobs.Unavailable is { } jobsProblem) unavailable.Add(jobsProblem);
        var visibleJobs = jobs.Items.Where(job => !job.Confidential).ToList();

        var entries = new List<JournalEntry>();
        var threads = candidates.Where(discussion => discussion.Type == "chat"
            && facts[discussion.Id].Active).ToList();

        // The notes first: a tick's own-work filter and a thread's links both come from them.
        var notes = await CollectNotesAsync(candidates, facts, byId, context, ct);

        var threadEntries = await Task.WhenAll(threads.Select(discussion =>
            BuildThreadAsync(discussion, facts[discussion.Id], window, visibleJobs, notes, byId, unavailable, context, ct)));
        entries.AddRange(threadEntries);
        entries.AddRange(BuildTicks(candidates, facts, notes));

        IReadOnlyList<JournalAutomationInfo>? automations = null;
        if (mayReadAgent)
        {
            try { automations = await CachedPeerAsync("automations", context, () => peers.AutomationsAsync(context, ct)); }
            catch (JournalSourceUnavailableException ex) { unavailable.Add(ex.SourceName); }
        }
        var automationItems = await BuildAutomationsAsync(agent, visibleJobs, automations, context, ct);
        entries.AddRange(automationItems);

        entries = JournalProjection.GroupAutomations(
            entries.OrderBy(entry => entry.At).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToList()).ToList();

        return new JournalDayResponse
        {
            Agent = agentId,
            Day = Format(day),
            Today = Format(today),
            Start = window.Start,
            End = window.End,
            TimeZone = zone.Id,
            PreviousDay = Format(day.AddDays(-1)),
            NextDay = day < today ? Format(day.AddDays(1)) : null,
            Header = mayReadAgent ? await BuildHeaderAsync(agent, window, unavailable, context, ct) : null,
            Summary = BuildSummary(agentId, candidates, facts, visibleJobs, entries, byId),
            Counts = JournalProjection.Count(entries),
            Entries = entries,
            Partial = unavailable.Count > 0,
            Unavailable = unavailable.OrderBy(name => name, StringComparer.Ordinal).ToList(),
        };
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Which notes reached which discussion that day, only between discussions this caller may read.</summary>
    private async Task<List<JournalNote>> CollectNotesAsync(
        IReadOnlyList<DiscussionRead> candidates, IReadOnlyDictionary<string, DayFacts> facts,
        IReadOnlyDictionary<string, DiscussionRead> byId, HttpContext context, CancellationToken ct)
    {
        var result = new List<JournalNote>();
        foreach (var target in candidates)
        {
            var read = facts[target.Id];
            foreach (var note in read.NotesIn)
                if (await IsReadableElsewhereAsync(note.FromDiscussionId, byId, context, ct))
                    result.Add(note);

            // Before the stamp, a tick's note to Live says only that it came from a heartbeat.
            if (target.Type != "live") continue;
            foreach (var (uid, at) in read.UnstampedHeartbeatNotes)
            {
                var from = candidates.FirstOrDefault(discussion => discussion.Type == HeartbeatService.DiscussionType
                    && JournalProjection.TurnAt(facts[discussion.Id].Turns, at) is not null);
                if (from is not null) result.Add(new(from.Id, null, target.Id, uid, at, true));
            }
        }
        return result;
    }

    private async Task<bool> IsReadableElsewhereAsync(
        string discussionId, IReadOnlyDictionary<string, DiscussionRead> byId, HttpContext context, CancellationToken ct)
    {
        if (byId.ContainsKey(discussionId)) return true;
        var other = await store.GetAsync(discussionId, ct);
        return other is { Confidential: false } && DiscussionAccessPolicy.CanRead(other, context);
    }

    private async Task<JournalEntry> BuildThreadAsync(
        DiscussionRead discussion, DayFacts read, JournalWindow window, IReadOnlyList<JournalJob> jobs,
        IReadOnlyList<JournalNote> notes, IReadOnlyDictionary<string, DiscussionRead> byId,
        HashSet<string> unavailable, HttpContext context, CancellationToken ct)
    {
        var cause = await ReadCauseAsync(discussion, read, unavailable, ct);
        var ownJobs = jobs.Where(job => job.ContextId("discussion") == discussion.Id
            || job.Context.Any(item => item.Kind == "discussion" && item.Id == discussion.Id)).ToList();
        var failed = ownJobs.Where(job => job.HasFailed && JournalProjection.BackgroundGroup(job.Name) is null).ToList();
        var status = JournalProjection.ThreadStatus(discussion.Status);

        var links = new List<JournalLink> { new("discussion", discussion.Id) };
        foreach (var note in notes.Where(note => note.FromDiscussionId == discussion.Id))
            links.Add(NoteLink(note, byId));

        return new JournalEntry
        {
            Id = $"thread:{discussion.Id}",
            Kind = JournalKinds.Thread,
            Cause = cause?.Cause ?? JournalCauses.Unknown,
            Inferred = cause?.Inferred ?? false,
            At = read.FirstAt ?? window.Start,
            Title = DiscussionDisplayTitle.For(discussion) ?? "Untitled discussion",
            Status = status,
            Text = read.LastText,
            TextMessageUid = read.LastTextUid,
            DiscussionId = discussion.Id,
            Filters = JournalProjection.FiltersOf(JournalKinds.Thread, cause?.Cause ?? JournalCauses.Unknown, failed.Count, false),
            FailedJobs = failed.Count,
            Request = await BuildRequestAsync(cause, byId, context, ct),
            StartedFrom = await BuildStartedFromAsync(cause, byId, context, ct),
            Links = links,
            Sessions = await BuildSessionsAsync(read.Links, unavailable, context, ct),
            Jobs = failed.Select(job => new JournalJobRef(job.Id.ToString(), job.Name, job.Status, job.Capability)).ToList(),
            Artifacts = read.Artifacts,
            Commits = await BuildCommitsAsync(read, ownJobs, window, unavailable, context, ct),
        };
    }

    private static JournalLink NoteLink(JournalNote note, IReadOnlyDictionary<string, DiscussionRead> byId)
        => new("note-sent", note.ToDiscussionId, note.MessageUid,
            byId.TryGetValue(note.ToDiscussionId, out var to) ? DiscussionDisplayTitle.For(to) : null,
            Inferred: note.Inferred);

    private static IEnumerable<JournalEntry> BuildTicks(
        IReadOnlyList<DiscussionRead> candidates, IReadOnlyDictionary<string, DayFacts> facts, IReadOnlyList<JournalNote> notes)
    {
        var liveIds = candidates.Where(discussion => discussion.Type == "live").Select(discussion => discussion.Id).ToHashSet();
        var byIdForLinks = candidates.ToDictionary(discussion => discussion.Id, StringComparer.Ordinal);
        foreach (var heartbeat in candidates.Where(discussion => discussion.Type == HeartbeatService.DiscussionType))
        {
            var read = facts[heartbeat.Id];
            var fromHere = notes.Where(note => note.FromDiscussionId == heartbeat.Id).ToList();
            foreach (var turn in read.Turns)
            {
                var sent = fromHere.Where(note => JournalProjection.TurnAt(read.Turns, note.At)?.MessageUid == turn.MessageUid).ToList();
                var links = new List<JournalLink> { new("message", heartbeat.Id, turn.MessageUid, "Open tick") };
                links.AddRange(sent.Select(note => NoteLink(note, byIdForLinks)));
                var running = heartbeat.Status == "thinking" && turn.Text is null && !turn.Silent;
                var status = running ? "running" : turn.Silent ? "silent" : "idle";
                yield return new JournalEntry
                {
                    Id = $"tick:{heartbeat.Id}:{turn.MessageUid}",
                    Kind = JournalKinds.Tick,
                    Cause = JournalCauses.Tick,
                    At = turn.At,
                    Title = turn.Text is { } text ? JournalProjection.FirstSentence(text)
                        : turn.Silent ? "Stayed silent" : "Tick",
                    Status = status,
                    Text = turn.Text,
                    TextMessageUid = turn.MessageUid,
                    Silent = turn.Silent,
                    DiscussionId = heartbeat.Id,
                    Filters = JournalProjection.FiltersOf(JournalKinds.Tick, JournalCauses.Tick, 0,
                        sent.Any(note => liveIds.Contains(note.ToDiscussionId))),
                    Links = links,
                };
            }
        }
    }

    private async Task<IReadOnlyList<JournalEntry>> BuildAutomationsAsync(
        LeafEntity agent, IReadOnlyList<JournalJob> jobs,
        IReadOnlyList<JournalAutomationInfo>? automations, HttpContext context, CancellationToken ct)
    {
        var agentId = agent.Id.ToString();
        var heartbeatAutomation = HeartbeatService.AutomationId(agent.Data)?.ToString();
        var known = automations?.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var children = jobs.Where(job => job.ParentJobId is not null)
            .ToLookup(job => job.ParentJobId!.Value);

        var mine = jobs.Where(job => job.ParentJobId is null && job.ContextId("automation") is not null)
            .Select(job => (Job: job, AutomationId: job.ContextId("automation")!))
            .Where(item => !string.Equals(item.AutomationId, heartbeatAutomation, StringComparison.OrdinalIgnoreCase))
            .Where(item => known is not null && known.TryGetValue(item.AutomationId, out var info)
                // Known: its own Agent field decides, and one with no Agent is infrastructure.
                ? string.Equals(info.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                // No longer listed (a one-shot since removed): only its signed actor can say.
                : string.Equals(item.Job.AgentRef, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = await Task.WhenAll(mine.Select(async item =>
        {
            var (job, automationId) = item;
            var kids = children[job.Id].OrderBy(child => child.QueuedAt).ToList();
            var failed = kids.Where(child => child.HasFailed).Prepend(job).Where(candidate => candidate.HasFailed).ToList();
            var status = failed.Count > 0 ? "failed" : JournalProjection.JobStatusWord(job.Status);
            string? text = null;
            foreach (var session in Enumerable.Reverse(kids).Where(child => child.Capability == "ai-session"))
            {
                text = JournalProjection.OutcomeText(await ReadJobTextAsync(session, context, ct));
                if (text is not null) break;
            }
            text ??= failed.Select(candidate => candidate.ErrorMessage).FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));

            var title = JournalProjection.AutomationTitle(
                known is not null && known.TryGetValue(automationId, out var info) ? info.Name : null,
                job.Context.FirstOrDefault(context => context.Kind == "automation")?.Id);
            var refs = new List<JournalJobRef> { new(job.Id.ToString(), job.Name, job.Status, job.Capability) };
            refs.AddRange(failed.Where(candidate => candidate != job)
                .Select(candidate => new JournalJobRef(candidate.Id.ToString(), candidate.Name, candidate.Status, candidate.Capability)));
            return new JournalEntry
            {
                Id = $"automation:{job.Id}",
                Kind = JournalKinds.Automation,
                Cause = JournalCauses.Scheduled,
                At = job.QueuedAt,
                Title = title,
                Status = status,
                Text = text,
                AutomationId = automationId,
                FailedJobs = failed.Count,
                Filters = JournalProjection.FiltersOf(JournalKinds.Automation, JournalCauses.Scheduled, failed.Count, false),
                Links = [new("automation", AutomationId: automationId, Label: title), new("job", JobId: job.Id.ToString())],
                Jobs = refs,
            };
        }));
        return entries;
    }

    private async Task<string?> ReadJobTextAsync(JournalJob job, HttpContext context, CancellationToken ct)
    {
        var key = (Subject(context), job.Id);
        if (_jobTexts.TryGetValue(key, out var cached)) return cached;
        await _computeSlots.WaitAsync(ct);
        string? text = null;
        try
        {
            // The session's own result carries its last assistant text; /events stops at the first hundred.
            var reply = await compute.GetJobAsync(job.Id, ct);
            if (reply.StatusCode != 200) return null;
            using var document = JsonDocument.Parse(reply.Content);
            if (document.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("text", out var value)
                && value.ValueKind == JsonValueKind.String)
                text = value.GetString();
        }
        catch (JsonException) { return null; }
        catch (HttpRequestException) { return null; }
        finally { _computeSlots.Release(); }
        if (job.IsTerminal)
        {
            Bound(_jobTexts);
            _jobTexts[key] = text;
        }
        return text;
    }

    private async Task<JournalHeader?> BuildHeaderAsync(
        LeafEntity agent, JournalWindow window, HashSet<string> unavailable, HttpContext context, CancellationToken ct)
    {
        var outfit = await ReadOutfitAsync(agent, window, unavailable, ct);
        // Having a discussion with this Agent doesn't make its reasons and its mood anyone's to read.
        var owner = DiscussionAccessPolicy.IsInstallOwnerOrAdministrator(context.User);
        if (!owner) outfit = outfit is null ? null : outfit with { Reason = null };
        var mood = owner ? await ReadMoodAsync(agent, window, ct) : null;
        return outfit is null && mood is null ? null : new(outfit, mood);
    }

    private async Task<JournalOutfit?> ReadOutfitAsync(
        LeafEntity agent, JournalWindow window, HashSet<string> unavailable, CancellationToken ct)
    {
        try
        {
            var outfits = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = "outfit",
                DataEquals = new Dictionary<string, object?> { ["agent"] = agent.Id.ToString() },
                Limit = 500,
            }, ct);
            var worn = outfits.Where(outfit => window.Contains(outfit.CreatedAt)).MaxBy(outfit => outfit.CreatedAt);
            if (worn is null) return null;
            return new JournalOutfit(worn.Id.ToString(), worn.Name, Text(worn.Data, "asset"),
                Text(worn.Data, "reasoning"), worn.Data["nsfw"] is JsonValue nsfw && nsfw.TryGetValue<bool>(out var flag) && flag);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unavailable.Add("outfits");
            return null;
        }
    }

    private async Task<JournalMood?> ReadMoodAsync(LeafEntity agent, JournalWindow window, CancellationToken ct)
    {
        try
        {
            var root = await workspaces.ResolveAsync(agent.Id, ct);
            if (root is null) return null;
            var folder = Path.Combine(root, "memory", "dreaming");
            if (!Directory.Exists(folder)) return null;
            // The file is written by a dreaming session; its case isn't part of any contract.
            var path = Directory.EnumerateFiles(folder, "*.md").FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "mood.md", StringComparison.OrdinalIgnoreCase));
            return path is null ? null : JournalProjection.ReadMood(await File.ReadAllTextAsync(path, ct), window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static JournalSummary BuildSummary(
        string agentId, IReadOnlyList<DiscussionRead> candidates, IReadOnlyDictionary<string, DayFacts> facts,
        IReadOnlyList<JournalJob> jobs, IReadOnlyList<JournalEntry> entries, IReadOnlyDictionary<string, DiscussionRead> byId)
    {
        var live = candidates.Where(discussion => discussion.Type == "live")
            .OrderByDescending(discussion => facts[discussion.Id].Messages)
            .ThenByDescending(discussion => discussion.CreatedAt)
            .FirstOrDefault();

        bool Mine(JournalJob job) => job.AgentRef is null || string.Equals(job.AgentRef, agentId, StringComparison.OrdinalIgnoreCase);

        var discord = jobs.Count(job => job.Name == JournalProjection.DiscordOutboundJob && Mine(job));

        // Everything an entry shows is its own; a job for a discussion this caller can't read is no count at all.
        var represented = new HashSet<Guid>();
        foreach (var entry in entries)
            foreach (var job in entry.Jobs)
                if (Guid.TryParse(job.Id, out var id)) represented.Add(id);

        var automationJobs = jobs.Where(job => job.ContextId("automation") is not null && Mine(job)).ToList();
        foreach (var job in automationJobs) represented.Add(job.Id);

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!Mine(job)) continue;
            var discussion = job.Context.FirstOrDefault(item => item.Kind == "discussion")?.Id;
            var group = JournalProjection.BackgroundGroup(job.Name);
            if (group is null)
            {
                if (represented.Contains(job.Id)
                    || (job.ParentJobId is { } parent && represented.Contains(parent))) continue;
                if (discussion is not null) continue;
                group = "Other";
            }
            else if (discussion is not null && !byId.ContainsKey(discussion)) continue;
            groups[group] = groups.GetValueOrDefault(group) + 1;
        }

        return new(live is null ? null : new(live.Id, facts[live.Id].Messages),
            discord > 0 ? discord : null,
            new[] { "Sentinel", "Titles", "Pixoo", "Other" }
                .Where(groups.ContainsKey)
                .Select(group => new JournalBackgroundCount(group, groups[group]))
                .ToList());
    }

    private async Task<DayFacts> ReadFactsAsync(DiscussionRead discussion, JournalWindow window, HttpContext context, CancellationToken ct)
    {
        var version = $"{discussion.Status}|{discussion.SessionId}|{discussion.MessageCount}|{discussion.ConversationRevision}|{discussion.LastActivity:O}";
        var key = (Subject(context), discussion.EntityId, window.Day, version);
        if (_facts.TryGetValue(key, out var kept)) return kept;

        var read = await AnalyseAsync(discussion, window, ct);
        // Running, its records are still arriving; failed, a retry should get its chance.
        if (discussion.Status != "thinking" && read.Unavailable.Count == 0)
        {
            Bound(_facts);
            _facts[key] = read;
        }
        return read;
    }

    private async Task<DayFacts> AnalyseAsync(DiscussionRead discussion, JournalWindow window, CancellationToken ct)
    {
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var overlays = await ReadOverlayWindowAsync(discussion.EntityId, window, unavailable, ct);
        var sessionBacked = discussion.SessionId is not null;

        IReadOnlyList<SessionMessage> canonical = [];
        SessionMessage? canonicalFirst = null;
        var walkedToStart = false;
        var transcriptDenied = false;
        if (sessionBacked)
        {
            var walker = new TranscriptWalker(compute, _computeSlots, discussion.SessionId!, window);
            try
            {
                await walker.WalkBackToWindowStartAsync(ct);
                canonical = walker.InWindow;
                // Its cause needs the first record anyway: carry on down the pages already open.
                if (discussion.Type == "chat" && !_causes.ContainsKey(discussion.EntityId))
                {
                    canonicalFirst = await walker.ReadFirstAsync(ct);
                    walkedToStart = true;
                }
                transcriptDenied = walker.Denied;
            }
            catch (JournalSourceUnavailableException ex) { unavailable.Add(ex.SourceName); }
        }

        var visibleOverlays = overlays.Where(message => DiscussionEndpoints.IsHistoryOverlaySourceVisible(
            Text(message.Metadata, "source"), sessionBacked, allowUserBridges: true)).ToList();
        var visibleCanonical = canonical.Where(JournalProjection.IsVisible).ToList();

        DateTimeOffset? firstAt = null;
        foreach (var at in visibleCanonical.Select(JournalProjection.At).Concat(visibleOverlays.Select(message => message.CreatedAt)))
            if (firstAt is null || at < firstAt) firstAt = at;

        var spoken = JournalProjection.LastSpoken(canonical);
        string? lastText = spoken is null ? null : NovaNoReply.WithoutTrailingMarker(spoken.Content), lastUid = spoken?.MessageUid;
        // A delivery discussion's words live only in the overlay, as Nova's own bridge records.
        var bridged = overlays.LastOrDefault(message => message.Role == "assistant"
            && Text(message.Metadata, "source") == "nova-message" && !NovaNoReply.IsMarker(message.Content)
            && !string.IsNullOrWhiteSpace(message.Content));
        if (bridged is not null && (spoken is null || bridged.CreatedAt > JournalProjection.At(spoken)))
            (lastText, lastUid) = (NovaNoReply.WithoutTrailingMarker(bridged.Content), Text(bridged.Metadata, "uid"));

        // One message however many records hold it: a turn's uid, and the overlay copy of a framed event.
        var spokenUids = canonical
            .Where(message => message.EventType == "text" && message.Role is "user" or "assistant"
                && !string.IsNullOrWhiteSpace(message.Content) && !NovaNoReply.IsMarker(message.Content))
            .Select(message => message.MessageUid ?? $"record:{message.Id}");
        var overlayUids = visibleOverlays.Select(message => Text(message.Metadata, "uid") ?? $"overlay:{message.Id}");
        var messageCount = spokenUids.Concat(overlayUids).Distinct(StringComparer.Ordinal).Count();

        var notes = new List<JournalNote>();
        var heartbeatNotes = new List<(string?, DateTimeOffset)>();
        foreach (var message in overlays)
        {
            if (JournalProjection.NoteOf(message, discussion.Id) is { } note) notes.Add(note);
            else if (discussion.Type == "live" && Text(message.Metadata, "source") == "event:heartbeat"
                && EventSender.Read(message.Metadata) is null)
                heartbeatNotes.Add((Text(message.Metadata, "uid"), message.CreatedAt));
        }

        var links = overlays.Select(DelegationActivity.ParseLink).Where(link => link is not null).Select(link => link!)
            .GroupBy(link => link.SessionId, StringComparer.Ordinal).Select(group => group.Last()).ToList();

        return new DayFacts(
            firstAt is not null, firstAt, lastText, lastUid, messageCount,
            discussion.Type == HeartbeatService.DiscussionType ? JournalProjection.Turns(canonical) : [],
            notes, heartbeatNotes, links,
            canonical.Where(JournalProjection.IsAssistantText).Select(message => message.Content!).ToList(),
            discussion.Type == "chat" && firstAt is not null
                ? await ReadArtifactsAsync(discussion, window, unavailable, ct) : [],
            unavailable, walkedToStart, canonicalFirst, transcriptDenied);
    }

    private async Task<IReadOnlyList<DiscussionMessage>> ReadOverlayWindowAsync(
        Guid entityId, JournalWindow window, HashSet<string> unavailable, CancellationToken ct)
    {
        try
        {
            var from = new DiscussionMessagePageBoundary(window.Start, 0);
            var to = new DiscussionMessagePageBoundary(window.End, 0);
            var pages = new List<IReadOnlyList<DiscussionMessage>>();
            long? before = null;
            while (true)
            {
                DiscussionMessagePage page;
                await _overlaySlots.WaitAsync(ct);
                try
                {
                    page = await messages.GetMessagePageAsync(entityId,
                        new DiscussionMessagePageRequest(1000, before, null, from, to), ct);
                }
                finally { _overlaySlots.Release(); }
                pages.Add(page.Messages);
                if (!page.HasEarlier || page.OldestId is null || page.OldestId == before) break;
                before = page.OldestId;
            }
            pages.Reverse();
            return pages.SelectMany(page => page).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unavailable.Add("discussions");
            return [];
        }
    }

    private async Task<IReadOnlyList<JournalArtifact>> ReadArtifactsAsync(
        DiscussionRead discussion, JournalWindow window, HashSet<string> unavailable, CancellationToken ct)
    {
        if (discussion.SessionId is not { } sessionId) return [];
        var found = new List<JournalArtifact>();
        string? cursor = null;
        try
        {
            for (var page = 0; page < ArtifactPages; page++)
            {
                RedComputeClient.ProxyResult reply;
                await _computeSlots.WaitAsync(ct);
                try { reply = await compute.GetSessionArtifactsAsync(sessionId, null, null, null, "200", cursor, ct); }
                finally { _computeSlots.Release(); }

                if (reply.StatusCode == 404) return found;
                if (reply.StatusCode != 200) { unavailable.Add("artifacts"); return found; }
                var rewritten = DiscussionEndpoints.RewriteArtifactTargets(reply.Content, discussion.Id, sessionId);
                if (rewritten is null) { unavailable.Add("artifacts"); return found; }
                var root = JsonNode.Parse(rewritten)!.AsObject();
                foreach (var item in root["items"]!.AsArray().OfType<JsonObject>())
                {
                    var first = item["first"] as JsonObject;
                    if (!DateTimeOffset.TryParse(Text(first, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        || !window.Contains(at)) continue;
                    // What the Agent produced: not the person's own attachments, and not every address it mentioned.
                    if (Text(item, "role") == "user" || Text(item, "kind") == "link") continue;
                    if (Text(item, "key") is not { } key || Text(item, "kind") is not { } kind || Text(item, "target") is not { } target) continue;
                    found.Add(new(key, kind, target, Text(item, "title"), Text(item, "mediaType"), Text(first, "messageUid"), at));
                }
                cursor = Text(root["page"] as JsonObject, "nextCursor");
                if (cursor is null) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            unavailable.Add("artifacts");
        }
        return found.OrderByDescending(artifact => artifact.Kind == "image").ThenBy(artifact => artifact.At).Take(MaxArtifacts).ToList();
    }

    private async Task<JournalCauseFacts?> ReadCauseAsync(
        DiscussionRead discussion, DayFacts read, HashSet<string> unavailable, CancellationToken ct)
    {
        var (cause, problem) = await ResolveCauseAsync(discussion, read, ct);
        if (problem is not null) lock (unavailable) unavailable.Add(problem);
        return cause;
    }

    /// <summary>
    /// Only the true first record decides. <c>transcript-page</c> reads newest first, so this walks to
    /// its start: stopping early would take a later callback for the record that opened the thread.
    /// </summary>
    private async Task<(JournalCauseFacts? Cause, string? Problem)> ResolveCauseAsync(
        DiscussionRead discussion, DayFacts read, CancellationToken ct)
    {
        if (_causes.TryGetValue(discussion.EntityId, out var known)) return (known, null);

        IReadOnlyList<DiscussionMessage> overlayHead;
        try { overlayHead = await ReadOverlayHeadAsync(discussion, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, "discussions"); }

        var canonicalFirst = read.CanonicalFirst;
        var denied = read.TranscriptDenied;
        if (!read.WalkedToStart && discussion.SessionId is { } sessionId)
        {
            var walker = new TranscriptWalker(compute, _computeSlots, sessionId, null);
            try { canonicalFirst = await walker.ReadFirstAsync(ct); }
            catch (JournalSourceUnavailableException ex) { return (null, ex.SourceName); }
            denied |= walker.Denied;
        }

        var first = JournalProjection.PickFirst(overlayHead, canonicalFirst);
        var cause = JournalProjection.DecideCause(first, await StartedByAutomationAsync(discussion, ct));

        // An empty discussion hasn't got a first record yet; ask again next time. A caller who was
        // refused the transcript hasn't seen the first record either, so their answer isn't kept for all.
        if (first is not null && !denied)
        {
            Bound(_causes);
            _causes[discussion.EntityId] = cause;
        }
        return (cause, null);
    }

    private async Task<bool> StartedByAutomationAsync(DiscussionRead discussion, CancellationToken ct)
    {
        var entity = await entities.GetAsync(discussion.EntityId, ct);
        return entity?.Data["automation_attempt_job_id"] is JsonValue value
            && value.TryGetValue<string>(out var attempt) && !string.IsNullOrWhiteSpace(attempt);
    }

    private async Task<IReadOnlyList<DiscussionMessage>> ReadOverlayHeadAsync(DiscussionRead discussion, CancellationToken ct)
    {
        await _overlaySlots.WaitAsync(ct);
        try { return await messages.GetMessagesAsync(discussion.EntityId, 20, 0, ct); }
        finally { _overlaySlots.Release(); }
    }

    /// <summary>
    /// A calendar dot, not a claim: the settled cause when a day view has found it, else what the
    /// overlay's first record says. Reading a month's transcripts to colour a dot isn't worth their
    /// cost; a thread whose transcript opens before its overlay can show a different dot until read.
    /// </summary>
    private async Task<(string Cause, string? Problem)> ReadCalendarCauseAsync(DiscussionRead discussion, CancellationToken ct)
    {
        if (_causes.TryGetValue(discussion.EntityId, out var settled)) return (settled.Cause, null);
        if (_overlayCauses.TryGetValue(discussion.EntityId, out var cached)) return (cached, null);
        try
        {
            var head = await ReadOverlayHeadAsync(discussion, ct);
            var first = JournalProjection.PickFirst(head, null);
            // Only this rule depends on it, so only this case pays for the entity read.
            var automated = first?.Source == "nova-message" && await StartedByAutomationAsync(discussion, ct);
            var cause = JournalProjection.DecideCause(first, automated).Cause;
            if (head.Count > 0)
            {
                Bound(_overlayCauses);
                _overlayCauses[discussion.EntityId] = cause;
            }
            return (cause, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (JournalCauses.Unknown, "discussions"); }
    }

    private async Task<JournalRequest?> BuildRequestAsync(
        JournalCauseFacts? cause, IReadOnlyDictionary<string, DiscussionRead> byId, HttpContext context, CancellationToken ct)
    {
        if (cause?.Request is not { } request) return null;
        // Checked on every read: the place he asked may have become confidential since.
        var from = byId.TryGetValue(request.DiscussionId, out var listed) ? listed : await store.GetAsync(request.DiscussionId, ct);
        if (from is null or { Confidential: true } || !DiscussionAccessPolicy.CanRead(from, context)) return null;

        var key = (request.DiscussionId, request.MessageUid);
        if (_quotes.TryGetValue(key, out var quoted)) return quoted;
        var (quote, at, settled) = await FindMessageAsync(from, request.MessageUid, ct);
        var result = new JournalRequest(request.DiscussionId, request.MessageUid, quote, at);
        if (settled)
        {
            Bound(_quotes);
            _quotes[key] = result;
        }
        return result;
    }

    /// <summary>
    /// A person's message in a session-backed discussion lives in the transcript, a bridged one in the
    /// overlay. Newest first, bounded: an old enough citation loses its quote and keeps its link.
    /// </summary>
    private async Task<(string? Quote, DateTimeOffset? At, bool Settled)> FindMessageAsync(
        DiscussionRead discussion, string uid, CancellationToken ct)
    {
        long after = 0;
        for (var page = 0; page < 10; page++)
        {
            var records = await messages.GetMessagesAsync(discussion.EntityId, 1000, after, ct);
            if (records.FirstOrDefault(record => Text(record.Metadata, "uid") == uid) is { } found)
                return (JournalProjection.Quote(found.Content), found.CreatedAt, true);
            if (records.Count < 1000) break;
            after = records.Max(record => record.Id);
        }

        if (discussion.SessionId is not { } sessionId) return (null, null, true);
        string? cursor = null;
        for (var page = 0; page < MaxQuotePages; page++)
        {
            SessionTranscriptPageResult result;
            await _computeSlots.WaitAsync(ct);
            try { result = await compute.GetTranscriptPageAsync(sessionId, TranscriptPageSize, before: cursor, ct: ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (null, null, false);
            }
            finally { _computeSlots.Release(); }

            if (result.StatusCode == 404) return (null, null, true);
            // A refusal is about this caller, not about the message: it mustn't settle the quote for others.
            if (!result.Success) return (null, null, false);
            var found = result.Value!.Messages.FirstOrDefault(message => message.MessageUid == uid
                && message.EventType == "text" && !string.IsNullOrWhiteSpace(message.Content));
            if (found is not null) return (JournalProjection.Quote(found.Content), JournalProjection.At(found), true);
            cursor = result.Value.Page.OldestCursor;
            if (!result.Value.Page.HasEarlier || cursor is null) break;
        }
        return (null, null, true);
    }

    private async Task<JournalStartedFrom?> BuildStartedFromAsync(
        JournalCauseFacts? cause, IReadOnlyDictionary<string, DiscussionRead> byId, HttpContext context, CancellationToken ct)
    {
        if (cause?.First is not { } first) return null;
        var stampedFrom = first.Stamped?.DiscussionId;
        var from = stampedFrom ?? (first.Source?.StartsWith("event:", StringComparison.Ordinal) == true
            ? JournalProjection.SenderOfSource(first.Source) : null);
        if (from is null || !await IsReadableElsewhereAsync(from, byId, context, ct)) return null;
        var title = byId.TryGetValue(from, out var listed)
            ? DiscussionDisplayTitle.For(listed)
            : first.Stamped?.Title;
        return new(from, title, first.Uid, stampedFrom is null);
    }

    private async Task<IReadOnlyList<JournalSession>> BuildSessionsAsync(
        IReadOnlyList<DelegationLink> links, HashSet<string> unavailable, HttpContext context, CancellationToken ct)
    {
        var sessions = await Task.WhenAll(links.Select(async link =>
        {
            var snapshot = await ReadSessionAsync(link.SessionId, context, ct);
            if (snapshot.Denied) return null;
            if (snapshot.Value is not { } state)
            {
                lock (unavailable) unavailable.Add("sessions");
                return new JournalSession(link.SessionId, link.Repository, null, "unknown");
            }
            if (state.Confidential) return null;
            return new JournalSession(link.SessionId, link.Repository, state.Title, DelegationActivity.ProjectStatus(state));
        }));
        return sessions.Where(session => session is not null).Select(session => session!).ToList();
    }

    private async Task<RedComputeClient.DelegationSnapshotResult> ReadSessionAsync(string sessionId, HttpContext context, CancellationToken ct)
    {
        var key = $"{Subject(context)}|{sessionId}";
        var now = _time.GetUtcNow();
        if (_sessions.TryGetValue(key, out var hit) && now - hit.ReadAt < SessionFreshness) return hit.Value;
        await _computeSlots.WaitAsync(ct);
        try
        {
            var snapshot = await compute.GetDelegationSnapshotAsync(sessionId, ct);
            Bound(_sessions);
            _sessions[key] = new(snapshot, _time.GetUtcNow());
            return snapshot;
        }
        finally { _computeSlots.Release(); }
    }

    /// <summary>
    /// Only hashes that literally appear in the thread's own words and resolve to a real commit, looked
    /// up in the repositories its jobs touched and the few its words name. No match, no commit: time
    /// windows attribute nothing here.
    /// </summary>
    private async Task<IReadOnlyList<JournalCommit>> BuildCommitsAsync(
        DayFacts read, IReadOnlyList<JournalJob> ownJobs, JournalWindow window,
        HashSet<string> unavailable, HttpContext context, CancellationToken ct)
    {
        var candidates = JournalProjection.HashCandidates(read.AssistantTexts);
        if (candidates.Count == 0) return [];

        IReadOnlyList<JournalRepositoryInfo>? repositories;
        try { repositories = await CachedPeerAsync("repositories", context, () => peers.RepositoriesAsync(context, ct)); }
        catch (JournalSourceUnavailableException ex)
        {
            lock (unavailable) unavailable.Add(ex.SourceName);
            return [];
        }
        if (repositories is null) return [];

        var touched = ownJobs.SelectMany(job => job.Context)
            .Where(item => item.Kind == "repository")
            .SelectMany(item => new[] { item.Id, item.EntityId })
            .Concat(read.Links.SelectMany(link => new[] { link.RepositoryId, link.Repository }))
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Touched(JournalRepositoryInfo repository)
            => touched.Contains(repository.Id) || (repository.Slug is not null && touched.Contains(repository.Slug))
                || touched.Contains(repository.Name);

        var words = string.Join('\n', read.AssistantTexts);
        var scoped = repositories.Where(Touched)
            .Concat(repositories.Where(repository => !Touched(repository) && NamedIn(words, repository.Name)).Take(MaxNamedRepositories))
            .Take(MaxCommitRepositories)
            .ToList();

        var since = window.Start - CommitLookback;
        var remaining = candidates.ToHashSet(StringComparer.Ordinal);
        var result = new List<JournalCommit>();
        foreach (var repository in scoped)
        {
            if (remaining.Count == 0) break;
            IReadOnlyList<JournalCommitInfo>? commits;
            try { commits = await ReadCommitsAsync(repository, since, context, ct); }
            catch (JournalSourceUnavailableException ex)
            {
                lock (unavailable) unavailable.Add(ex.SourceName);
                continue;
            }
            if (commits is null) continue;
            foreach (var candidate in remaining.ToList())
            {
                if (commits.FirstOrDefault(commit => commit.Hash.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) is not { } commit)
                    continue;
                remaining.Remove(candidate);
                if (result.Any(existing => existing.Hash == commit.Hash)) continue;
                var remote = JournalProjection.RemoteBase(repository.RemoteUrl);
                result.Add(new(commit.Hash, commit.ShortHash ?? commit.Hash[..Math.Min(7, commit.Hash.Length)], commit.Message,
                    repository.Name, remote is null ? null : $"{remote}/commit/{commit.Hash}"));
            }
        }
        return result;
    }

    private static bool NamedIn(string words, string name)
        => name.Length >= 3 && Regex.IsMatch(words, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private async Task<IReadOnlyList<JournalCommitInfo>?> ReadCommitsAsync(
        JournalRepositoryInfo repository, DateTimeOffset since, HttpContext context, CancellationToken ct)
    {
        var key = $"{Subject(context)}|commits|{repository.Id}|{since:yyyy-MM-dd}";
        if (_peerCache.TryGetValue(key, out var hit) && _time.GetUtcNow() - hit.ReadAt < CommitFreshness)
            return (IReadOnlyList<JournalCommitInfo>?)hit.Value;
        await _codeSlots.WaitAsync(ct);
        try
        {
            var commits = await peers.CommitsAsync(context, repository.Slug ?? repository.Id, since, ct);
            Bound(_peerCache);
            _peerCache[key] = new(commits, _time.GetUtcNow());
            return commits;
        }
        finally { _codeSlots.Release(); }
    }

    /// <summary>The neighbours answer for the caller's own credentials, so each caller keeps their own answer.</summary>
    private async Task<T?> CachedPeerAsync<T>(string name, HttpContext context, Func<Task<T?>> read) where T : class
    {
        var key = $"{Subject(context)}|{name}";
        if (_peerCache.TryGetValue(key, out var hit) && _time.GetUtcNow() - hit.ReadAt < PeerFreshness)
            return (T?)hit.Value;
        var value = await read();
        Bound(_peerCache);
        _peerCache[key] = new(value, _time.GetUtcNow());
        return value;
    }

    private async Task<JobsRead> ReadJobsAsync(JournalWindow window, bool isToday, HttpContext context, CancellationToken ct)
    {
        var key = $"{Subject(context)}|{window.Day:yyyy-MM-dd}";
        var freshness = isToday ? TodayFreshness : ClosedDayJobsFreshness;
        if (_jobs.TryGetValue(key, out var hit) && _time.GetUtcNow() - hit.ReadAt < freshness) return hit.Value;
        var read = await ListJobsAsync(window.Start, window.End, null, null, ct);
        if (read.Unavailable is null)
        {
            Bound(_jobs);
            _jobs[key] = new(read, _time.GetUtcNow());
        }
        return read;
    }

    /// <summary>Every job in the range. A RedCompute without the date filter is a short read, never a wrong day.</summary>
    private async Task<JobsRead> ListJobsAsync(
        DateTimeOffset since, DateTimeOffset until, string? capability, string? contextKind, CancellationToken ct, string? status = null)
    {
        var items = new List<JournalJob>();
        for (var page = 0; page < MaxJobPages; page++)
        {
            RedComputeClient.ProxyResult reply;
            await _computeSlots.WaitAsync(ct);
            try { reply = await compute.ListJobsAsync(since, until, JobsPerPage, page * JobsPerPage, capability, contextKind, status, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new(items, "jobs");
            }
            finally { _computeSlots.Release(); }

            if (reply.StatusCode != 200) return new(items, "jobs");
            List<JournalJob> parsed;
            try { parsed = ParseJobs(reply.Content); }
            catch (JsonException) { return new(items, "jobs"); }

            var inRange = parsed.Where(job => job.QueuedAt >= since && job.QueuedAt < until).ToList();
            items.AddRange(inRange);
            if (inRange.Count != parsed.Count) return new(items, "jobs");
            if (parsed.Count < JobsPerPage) return new(items, null);
        }
        return new(items, "jobs");
    }

    internal static List<JournalJob> ParseJobs(string content)
    {
        using var document = JsonDocument.Parse(content);
        var result = new List<JournalJob>();
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idNode) || !idNode.TryGetGuid(out var id)
                || !item.TryGetProperty("queuedAt", out var queued) || !queued.TryGetDateTimeOffset(out var queuedAt))
                continue;
            Guid? parent = item.TryGetProperty("parentJobId", out var parentNode)
                && parentNode.ValueKind == JsonValueKind.String && parentNode.TryGetGuid(out var parentId) ? parentId : null;

            string? actorKind = null, actorEntity = null;
            var context = new List<JournalContextRef>();
            if (item.TryGetProperty("creationProvenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object)
            {
                if (provenance.TryGetProperty("actor", out var actor))
                {
                    actorKind = DelegationActivity.String(actor, "kind");
                    actorEntity = DelegationActivity.String(actor, "entityId");
                }
                if (provenance.TryGetProperty("context", out var references) && references.ValueKind == JsonValueKind.Array)
                    foreach (var reference in references.EnumerateArray())
                        if (DelegationActivity.String(reference, "kind") is { } kind)
                            context.Add(new(kind, DelegationActivity.String(reference, "id"), DelegationActivity.String(reference, "entityId")));
            }

            result.Add(new(id, DelegationActivity.String(item, "name"), DelegationActivity.String(item, "capability"),
                DelegationActivity.String(item, "status") ?? "Unknown", queuedAt, parent,
                item.TryGetProperty("confidential", out var confidential) && confidential.ValueKind == JsonValueKind.True,
                DelegationActivity.String(item, "errorMessage"), actorKind, actorEntity, context));
        }
        return result;
    }

    /// <summary>
    /// The calendar: from the discussion list, each discussion's cause as far as it is known, and the
    /// month's jobs. It reads no transcript.
    /// </summary>
    private async Task<JournalMonthResponse> BuildMonthAsync(
        LeafEntity agent, DateOnly first, DateOnly today, TimeZoneInfo zone, HttpContext context, CancellationToken ct)
    {
        var agentId = agent.Id.ToString();
        var start = JournalWindow.For(first, zone).Start;
        var end = JournalWindow.For(first.AddMonths(1), zone).Start;
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var days = new Dictionary<DateOnly, (bool Own, bool WithYou, bool Failed)>();

        void Mark(DateTimeOffset at, bool own = false, bool withYou = false, bool failed = false)
        {
            if (at < start || at >= end) return;
            var day = JournalWindow.DayOf(at, zone);
            if (day > today) return;
            var current = days.GetValueOrDefault(day);
            days[day] = (current.Own || own, current.WithYou || withYou, current.Failed || failed);
        }

        var readable = await ReadableAsync(agent, context, ct);
        var inMonth = readable.Where(discussion => Utc(discussion.CreatedAt) < end && Utc(discussion.LastActivity) >= start).ToList();
        var chats = inMonth.Where(discussion => discussion.Type == "chat").ToList();

        var causes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Task.WhenAll(chats.Select(async discussion =>
        {
            var (cause, problem) = await ReadCalendarCauseAsync(discussion, ct);
            if (problem is not null) lock (unavailable) unavailable.Add(problem);
            causes[discussion.Id] = cause;
        }));

        void MarkDiscussion(string id, DateTimeOffset at)
        {
            var cause = causes.GetValueOrDefault(id, JournalCauses.Unknown);
            Mark(at, cause == JournalCauses.Own, cause is JournalCauses.WithYou or JournalCauses.Asked);
        }

        foreach (var discussion in inMonth)
        {
            MarkDiscussion(discussion.Id, Utc(discussion.CreatedAt));
            MarkDiscussion(discussion.Id, Utc(discussion.LastActivity));
        }

        if (readable.Count > 0)
        {
            var byId = readable.ToDictionary(discussion => discussion.Id, StringComparer.Ordinal);
            // A thread's own jobs place it on the days between its first and last activity.
            var sessions = await ListJobsAsync(start, end, null, "discussion", ct);
            if (sessions.Unavailable is { } sessionProblem) unavailable.Add(sessionProblem);
            foreach (var job in sessions.Items.Where(job => !job.Confidential && JournalProjection.BackgroundGroup(job.Name) is null))
            {
                if (job.Context.FirstOrDefault(item => item.Kind == "discussion")?.Id is { } id && byId.TryGetValue(id, out var discussion))
                {
                    if (discussion.Type == "chat") MarkDiscussion(id, job.QueuedAt);
                    else Mark(job.QueuedAt);
                }
            }

            IReadOnlyList<JournalAutomationInfo>? automations = null;
            try { automations = await CachedPeerAsync("automations", context, () => peers.AutomationsAsync(context, ct)); }
            catch (JournalSourceUnavailableException ex) { unavailable.Add(ex.SourceName); }
            var known = automations?.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var workflows = await ListJobsAsync(start, end, "workflow", null, ct);
            if (workflows.Unavailable is { } workflowProblem) unavailable.Add(workflowProblem);
            // The failed mark means something scheduled failed, by the day view's rule: the run or a job
            // under it. A workflow can complete with a failed child, so its own status isn't enough.
            var heartbeatAutomation = HeartbeatService.AutomationId(agent.Data)?.ToString();
            var runs = new Dictionary<Guid, JournalJob>();
            foreach (var job in workflows.Items.Where(job => !job.Confidential && job.ParentJobId is null))
            {
                if (job.ContextId("automation") is not { } automationId
                    || string.Equals(automationId, heartbeatAutomation, StringComparison.OrdinalIgnoreCase)) continue;
                var mine = known is not null && known.TryGetValue(automationId, out var info)
                    ? string.Equals(info.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(job.AgentRef, agentId, StringComparison.OrdinalIgnoreCase);
                if (!mine) continue;
                runs[job.Id] = job;
                Mark(job.QueuedAt, failed: job.HasFailed);
            }
            foreach (var status in new[] { "Failed", "TimedOut" })
            {
                var failures = await ListJobsAsync(start, end, null, null, ct, status);
                if (failures.Unavailable is { } failureProblem) unavailable.Add(failureProblem);
                foreach (var job in failures.Items.Where(job => !job.Confidential))
                    if (job.ParentJobId is { } parent && runs.TryGetValue(parent, out var run))
                        Mark(run.QueuedAt, failed: true);
            }
        }

        return new JournalMonthResponse(agentId, first.ToString("yyyy-MM", CultureInfo.InvariantCulture), Format(today),
            days.OrderBy(pair => pair.Key)
                .Select(pair => new JournalCalendarDay(Format(pair.Key), pair.Value.Own, pair.Value.WithYou, pair.Value.Failed))
                .ToList(),
            unavailable.Count > 0, unavailable.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    private static string? Text(JsonObject? data, string key)
        => data?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Reads a session transcript newest first, keeping only the window and the earliest record seen,
    /// so walking a long thread to its start doesn't hold the thread in memory.
    /// </summary>
    private sealed class TranscriptWalker(
        RedComputeClient compute, SemaphoreSlim slots, string sessionId, JournalWindow? window)
    {
        private readonly List<SessionMessage> _inWindow = [];
        private SessionMessage? _first;
        private string? _cursor;
        private bool _started;
        private bool _exhausted;
        private int _pages;

        public IReadOnlyList<SessionMessage> InWindow => _inWindow;

        /// <summary>RedCompute refused this caller, which says nothing of what the transcript holds.</summary>
        public bool Denied { get; private set; }

        public async Task WalkBackToWindowStartAsync(CancellationToken ct)
        {
            var start = window!.Value.Start;
            while (!_exhausted)
            {
                var (head, count) = await ReadPageAsync(ct);
                // Records aren't strictly in time order, so one old record doesn't end the walk.
                if (count > 0 && head.All(at => at < start)) break;
            }
        }

        public async Task<SessionMessage?> ReadFirstAsync(CancellationToken ct)
        {
            while (!_exhausted) await ReadPageAsync(ct);
            return _first;
        }

        private async Task<(IReadOnlyList<DateTimeOffset> Head, int Count)> ReadPageAsync(CancellationToken ct)
        {
            if (++_pages > MaxTranscriptPages)
                throw new JournalSourceUnavailableException("transcripts", $"Transcript {sessionId} is longer than a journal read walks");

            SessionTranscriptPageResult result;
            await slots.WaitAsync(ct);
            try { result = await compute.GetTranscriptPageAsync(sessionId, TranscriptPageSize, before: _started ? _cursor : null, ct: ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new JournalSourceUnavailableException("transcripts", ex.Message);
            }
            finally { slots.Release(); }

            // A session that no longer exists has nothing to add; the overlay still speaks for it.
            if (result.StatusCode == 404 || result.StatusCode == 403)
            {
                Denied = result.StatusCode == 403;
                _exhausted = true;
                return ([], 0);
            }
            if (!result.Success) throw new JournalSourceUnavailableException("transcripts", $"HTTP {result.StatusCode}");

            _started = true;
            var page = result.Value!;
            if (page.Messages.FirstOrDefault(IsOpening) is { } opening) _first = opening;
            if (window is { } range)
                _inWindow.InsertRange(0, page.Messages.Where(message => range.Contains(JournalProjection.At(message))));

            _cursor = page.Page.OldestCursor;
            if (!page.Page.HasEarlier || _cursor is null) _exhausted = true;
            return (page.Messages.Take(10).Select(JournalProjection.At).ToList(), page.Messages.Count);
        }

        private static bool IsOpening(SessionMessage message)
            => message.Role is "user" or "assistant" && message.EventType is "text" or "tool_use";
    }
}
