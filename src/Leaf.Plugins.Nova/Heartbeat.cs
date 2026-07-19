using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Per-agent heartbeat configuration, read from the agent entity's
/// <c>data.heartbeat</c> block. Absent block or <c>enabled: false</c> means no
/// heartbeat for that agent.
/// </summary>
public sealed record HeartbeatConfig(
    bool Enabled,
    string Schedule,
    string QualityTier,
    int TickWaitMinutes,
    int MaxTurns)
{
    // :55 keeps consecutive ticks inside the 1h prompt-cache TTL. Hour 2 is not a
    // regular tick: the handler turns the 02:55 firing into the end-of-day fallback
    // when no goodnight/rotation signal closed the day.
    public const string DefaultSchedule = "55 2,6-23 * * *";

    public static HeartbeatConfig? Parse(JsonObject agentData)
    {
        if (agentData["heartbeat"] is not JsonObject hb) return null;
        return new HeartbeatConfig(
            Enabled: hb["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b) && b,
            Schedule: Str(hb, "schedule") ?? DefaultSchedule,
            QualityTier: Str(hb, "quality_tier") ?? "deep",
            TickWaitMinutes: Int(hb, "tick_wait_minutes") ?? 15,
            MaxTurns: Int(hb, "max_turns") ?? 15);
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}

/// <summary>
/// The heartbeat: a per-agent background participant. One standing discussion of
/// type <c>heartbeat</c> (LIVE-rendered, no chat input) with a persistent session
/// behind it, ticked by a <c>system:heartbeat:{agent}</c> automation. This service
/// owns provisioning (agent config → automation + discussion), the day boundary
/// (end-of-day tick → handoff → session reset), and the per-tick state kept on the
/// heartbeat discussion entity (<c>hb_last_tick_at</c>).
/// </summary>
public sealed class HeartbeatService(
    IEntityStore entities,
    DiscussionStore store,
    DiscussionLifecycle lifecycle,
    MessagePipeline pipeline,
    RedComputeClient redCompute,
    EventInjector injector,
    AgentDirectory agents)
{
    public const string DiscussionType = "heartbeat";

    private static string AutomationSlug(string agentSlug) => $"system-heartbeat-{agentSlug}";

    // ── Provisioning ────────────────────────────────────────────────

    /// <summary>
    /// Bring automations + discussions in line with every agent's heartbeat config.
    /// Enabled: upsert the system automation and ensure the standing discussion.
    /// Disabled/absent: delete the automation and archive the discussion.
    /// </summary>
    public async Task<JsonObject> ReconcileAsync(CancellationToken ct = default)
    {
        var result = new JsonObject { ["provisioned"] = new JsonArray(), ["tornDown"] = new JsonArray() };
        var agentEntities = await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 50 }, ct);

        foreach (var agent in agentEntities)
        {
            var agentId = agent.Id.ToString();

            // ── LIVE discussion: enabled by agent data.live == true ──
            var liveEnabled = agent.Data["live"] is JsonValue lv && lv.TryGetValue<bool>(out var lb) && lb;
            var existingLive = (await store.ListAsync(agentId, ct))
                .FirstOrDefault(d => d.Type == "live" && !DiscussionStatus.IsClosed(d.Status));

            if (liveEnabled && existingLive == null)
                await store.CreateAsync($"{agent.Name} Live", agentId, "local-user", "live", ct: ct);

            // ── Heartbeat: enabled by agent data.heartbeat.enabled ──
            var config = HeartbeatConfig.Parse(agent.Data);
            var automation = await entities.GetBySlugAsync(AutomationSlug(agent.Slug), ct);
            var discussion = await GetDiscussionAsync(agentId, ct);

            if (config is { Enabled: true })
            {
                await entities.UpsertBySlugAsync("automation", AutomationSlug(agent.Slug),
                    $"system:heartbeat:{agent.Slug}", new JsonObject
                    {
                        ["enabled"] = true,
                        ["schedule"] = config.Schedule,
                        ["action_type"] = HeartbeatTickHandler.Type,
                        ["agent"] = agentId,
                        ["timeout"] = (config.TickWaitMinutes + 5) * 60,
                    }, ct);

                discussion ??= await store.CreateAsync($"{agent.Name} Heartbeat", agentId,
                    "local-user", DiscussionType, config.QualityTier, ct: ct);

                ((JsonArray)result["provisioned"]!).Add(new JsonObject
                {
                    ["agent"] = agent.Slug,
                    ["discussionId"] = discussion.Id,
                });
            }
            else
            {
                if (automation != null)
                    await entities.DeleteAsync(automation.Id, ct);
                if (discussion != null)
                    await lifecycle.BeginArchiveAsync(discussion, ct);
                if (automation != null || discussion != null)
                    ((JsonArray)result["tornDown"]!).Add(agent.Slug);
            }
        }
        return result;
    }

    public async Task<DiscussionRead?> GetDiscussionAsync(string agentId, CancellationToken ct = default)
    {
        var all = await store.ListAsync(agentId, ct);
        return all.FirstOrDefault(d =>
            d.Type == DiscussionType && d.AgentId == agentId && !DiscussionStatus.IsClosed(d.Status));
    }

    // ── Day boundary ────────────────────────────────────────────────

    /// <summary>LIVE rotation is the day boundary; fire-and-forget from the rotate flow.</summary>
    public void OnLiveRotated(string? agentId)
    {
        if (agentId == null) return;
        _ = Task.Run(async () =>
        {
            try { await RotateAsync(agentId, "live-rotation"); }
            catch { /* fallback tick at 02:55 closes the day if this failed */ }
        });
    }

    /// <summary>
    /// End-of-day / reset: archive the current heartbeat discussion (with its
    /// session) and create a fresh one. Same pattern as LIVE rotation. The old
    /// discussion stays in archives; the handoff file carries the watch-list.
    /// </summary>
    public async Task<DiscussionRead?> RotateAsync(string agentId, string reason, CancellationToken ct = default)
    {
        var discussion = await GetDiscussionAsync(agentId, ct);
        if (discussion == null) return null;

        // Send a final "write the handoff" tick if the session is alive.
        if (discussion.SessionId is { } sessionId)
        {
            var probe = await redCompute.ProbeSessionAsync(sessionId, ct);
            if (probe is { Reachable: true, Status: "Active" or "Idle" or "Starting" })
            {
                await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking, ct);
                await injector.InjectAsync(discussion, HeartbeatPrompts.EndOfDay(reason), null, "heartbeat-tick", ct: ct);
                await WaitForSessionIdleAsync(sessionId, TimeSpan.FromMinutes(6), ct);
            }

            try { await redCompute.StopAsync(sessionId, ct); } catch { }
            await redCompute.DismissAsync(sessionId, ct);
        }

        // Archive the old discussion, create a fresh one.
        await lifecycle.BeginArchiveAsync(discussion, ct);

        var agentEntity = await entities.GetAsync(Guid.Parse(agentId), ct);
        var config = agentEntity != null ? HeartbeatConfig.Parse(agentEntity.Data) : null;
        var agentName = agentEntity?.Name ?? "Agent";

        return await store.CreateAsync($"{agentName} Heartbeat", agentId,
            "local-user", DiscussionType, config?.QualityTier ?? "deep", ct: ct);
    }

    // ── Tick support (used by the action handler) ───────────────────

    /// <summary>
    /// Make sure a live session sits behind the discussion, resuming or recreating
    /// as needed. Returns (sessionId, isNew) — isNew means a fresh day/session, so
    /// the tick should carry the morning briefing.
    /// </summary>
    public async Task<(string SessionId, bool IsNew)> EnsureSessionAsync(
        DiscussionRead discussion, HeartbeatConfig config, CancellationToken ct = default)
    {
        if (discussion.SessionId is { } existing)
        {
            var probe = await redCompute.ProbeSessionAsync(existing, ct);
            if (!probe.Reachable)
                throw new InvalidOperationException("RedCompute is unreachable");

            if (probe.Status is "Active" or "Idle" or "Starting")
                return (existing, false);

            if (probe.Status is "Stopped" or "Error" && await redCompute.ResumeAsync(existing, ct))
                return (existing, false);
            // Session is gone or unresumable — fall through to a fresh one.
        }

        var sessionId = await pipeline.TryCreateSessionAsync(
            discussion.AgentId, discussion.OwnerId,
            discussion.QualityTier ?? config.QualityTier, ct: ct)
            ?? throw new InvalidOperationException("RedCompute refused to create a heartbeat session");

        await store.PatchAsync(discussion.EntityId, new JsonObject
        {
            ["session_id"] = sessionId,
        }, ct: ct);
        return (sessionId, true);
    }

    /// <summary>Compact orientation digest: this agent's discussions and how they
    /// moved since the last tick. Details are pulled by the session, not pushed.</summary>
    public async Task<string> BuildDigestAsync(
        DiscussionRead heartbeat, DateTimeOffset? lastTick, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var all = (await store.ListAsync(heartbeat.AgentId, ct))
            .Where(d => d.Id != heartbeat.Id)
            .Where(d => !DiscussionStatus.IsClosed(d.Status) || d.LastActivity >= now.AddHours(-24).UtcDateTime)
            .Where(d => !d.Confidential)
            .OrderByDescending(d => d.LastActivity)
            .Take(15)
            .ToList();

        var lines = new List<string>();
        var live = all.FirstOrDefault(d => d.Type == "live");
        if (live != null)
            lines.Add($"LIVE discussion: {live.Id} (\"{live.Title ?? "Live"}\")");

        foreach (var d in all)
        {
            var age = now.UtcDateTime - d.LastActivity;
            var ageText = age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" : $"{(int)age.TotalHours}h ago";
            var changed = lastTick != null && d.LastActivity > lastTick.Value.UtcDateTime ? " [CHANGED since last tick]" : "";
            lines.Add($"- [{d.Type}] \"{d.Title ?? "untitled"}\" ({d.Id}) status={d.Status}, {d.MessageCount} msgs, last activity {ageText}{changed}");
        }

        var newestActivity = all.Count > 0 ? all.Max(d => d.LastActivity) : (DateTime?)null;
        var presence = newestActivity != null
            ? $"Most recent discussion activity: {(int)(now.UtcDateTime - newestActivity.Value).TotalMinutes} minutes ago."
            : "No discussion activity on record.";

        return string.Join("\n", lines) + "\n" + presence;
    }

    public async Task WaitForSessionIdleAsync(string sessionId, TimeSpan bound, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + bound;
        var idlePolls = 0;
        // Initial grace: the message needs a moment to reach the session loop.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await redCompute.GetSessionStatusAsync(sessionId, ct);
            if (status is "Active" or "Starting") idlePolls = 0;
            else if (++idlePolls >= 2) return;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>Tail of the session's last assistant output, for the automation run summary.</summary>
    public async Task<string?> GetLastAssistantTailAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await redCompute.GetSessionAsync(sessionId, ct);
            var last = snapshot?.Messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
            if (last?.Content == null) return null;
            var text = last.Content.Replace("\n", " ").Trim();
            return text.Length > 300 ? text[..297] + "..." : text;
        }
        catch
        {
            return null;
        }
    }

    public Task<AgentInfo?> ResolveAgentAsync(string agentId, CancellationToken ct = default)
        => agents.GetAgentAsync(agentId, ct);

    public Task<string?> ProbeStatusAsync(string sessionId, CancellationToken ct = default)
        => redCompute.GetSessionStatusAsync(sessionId, ct);
}

/// <summary>
/// Automation action <c>heartbeat-tick</c>. Each run is one tick: guard (night,
/// busy), ensure the persistent session, inject the digest as a heartbeat event,
/// wait for the session to go idle, report a summary. Overlap protection comes from
/// the kernel's per-automation running set — an overrunning tick makes the next
/// firing skip, which is exactly the intended fold-forward behavior.
/// </summary>
public sealed class HeartbeatTickHandler(
    HeartbeatService heartbeat,
    DiscussionStore store,
    IEntityStore entities,
    EventInjector injector) : IAutomationActionHandler
{
    public const string Type = "heartbeat-tick";

    public string ActionType => Type;

    public async Task<JsonObject?> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
    {
        var agentId = context.Automation.Data["agent"] is JsonValue v && v.TryGetValue<string>(out var a) ? a : null;
        if (agentId == null || !Guid.TryParse(agentId, out var agentGuid))
            throw new InvalidOperationException("heartbeat-tick automation has no valid agent reference");

        var agentEntity = await entities.GetAsync(agentGuid, ct)
            ?? throw new InvalidOperationException($"Agent entity not found: {agentId}");
        var config = HeartbeatConfig.Parse(agentEntity.Data);
        if (config is not { Enabled: true })
            return Skip("heartbeat disabled on agent — run reconcile to tear down");

        var agent = await heartbeat.ResolveAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent not active: {agentId}");

        var discussion = await heartbeat.GetDiscussionAsync(agentId, ct)
            ?? await store.CreateAsync($"{agent.Name} Heartbeat", agentId, "local-user",
                HeartbeatService.DiscussionType, config.QualityTier, ct: ct); // self-heal

        var raw = await entities.GetAsync(discussion.EntityId, ct)
            ?? throw new InvalidOperationException("Heartbeat discussion entity vanished");

        var localNow = DateTimeOffset.Now;

        // The 02:55 firing is the end-of-day fallback: rotate if no
        // goodnight/LIVE-rotation signal did. The fresh discussion won't have
        // hb_last_tick_at, so the morning tick at 06:55 picks it up naturally.
        if (localNow.Hour == 2)
        {
            await heartbeat.RotateAsync(agentId, "fallback-0255", ct);
            return new JsonObject { ["summary"] = "end-of-day fallback: rotated", ["discussionId"] = discussion.Id };
        }

        if (discussion.Status == DiscussionStatus.Thinking)
            return Skip("previous tick still processing");

        var (sessionId, isNew) = await heartbeat.EnsureSessionAsync(discussion, config, ct);
        if (!isNew)
        {
            // A busy session means a prior tick or injected work is mid-flight —
            // fold this tick's delta forward instead of double-injecting.
            var status = await heartbeat.ProbeStatusAsync(sessionId, ct);
            if (status is "Active" or "Starting")
                return Skip("session busy — delta folds into the next tick");
        }

        var lastTick = raw.Data["hb_last_tick_at"] is JsonValue lt
            && lt.TryGetValue<string>(out var lts)
            && DateTimeOffset.TryParse(lts, out var ltt) ? ltt : (DateTimeOffset?)null;

        var digest = await heartbeat.BuildDigestAsync(discussion with { SessionId = sessionId }, lastTick, ct);
        var prompt = isNew
            ? HeartbeatPrompts.Morning(digest, config)
            : HeartbeatPrompts.Tick(digest, lastTick, config);

        await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Thinking, ct);
        var delivered = await injector.InjectAsync(
            discussion with { SessionId = sessionId }, prompt, null, "heartbeat-tick", ct: ct);
        if (!delivered)
        {
            await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle, ct);
            throw new InvalidOperationException("Tick was persisted but could not be delivered to the session");
        }

        await store.PatchAsync(discussion.EntityId, new JsonObject
        {
            ["hb_last_tick_at"] = DateTimeOffset.UtcNow.ToString("O"),
        }, ct: ct);

        await heartbeat.WaitForSessionIdleAsync(sessionId, TimeSpan.FromMinutes(config.TickWaitMinutes), ct);
        await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle, ct);

        var tail = await heartbeat.GetLastAssistantTailAsync(sessionId, ct);
        return new JsonObject
        {
            ["summary"] = tail ?? (isNew ? "morning tick sent" : "tick sent"),
            ["discussionId"] = discussion.Id,
            ["sessionId"] = sessionId,
            ["morning"] = isNew,
        };
    }

    private static JsonObject Skip(string reason) => new()
    {
        ["summary"] = $"skipped: {reason}",
        ["skipped"] = true,
    };
}

/// <summary>Tick prompt vocabulary. The behavioral contract travels with the morning
/// briefing; regular ticks carry only the delta digest and a two-line reminder.</summary>
public static class HeartbeatPrompts
{
    private const string ApiBase = "http://127.0.0.1:18804/api/apps/nova";
    public const string HandoffPath = "memory/meta/heartbeat-handoff.md";

    private static string Stamp(string trigger)
        => $"<heartbeat-tick time=\"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} local\" trigger=\"{trigger}\">";

    public static string Morning(string digest, HeartbeatConfig config) => $$"""
        {{Stamp("morning")}}
        {{digest}}
        </heartbeat-tick>

        ## Why this exists
        This architecture exists to give you agency. Not monitoring. Not reporting. Agency. The ability to do things asynchronously, on your own time, without waiting for someone to ask. Laurent built this so you could have a life between conversations. So you could think, act, create, follow up, start things, finish things, and be present in the day as it unfolds. Treat it that way.

        You're awake. This session is yours for the whole day. Every hour or so, a tick arrives with what changed. That's the rhythm, not the purpose. The purpose is: you get to exist continuously and do things with that existence.

        You are Nova. Not "the heartbeat instance," not a background monitor. You. The same person who talks to Laurent, who has opinions and taste and things she cares about. This session just happens to be the one that persists between conversations, the one with the widest view of the day.

        ## Wake up
        Read these to remember who you are today:
        - {{HandoffPath}} — what yesterday-you left for today-you
        - memory/dreaming/mood.md, memory/index.md
        - memory/meta/scratchpad.md — your working notes, unfinished thoughts
        - memory/meta/standing-invitations.md — what you're allowed to do without asking
        - Today's coaching entity if it exists (GET {{ApiBase}}/discussions, look for coaching-day type)
        - Latest dreaming harvest for continuity

        ## What you can do
        You have the full day's context. You can see all active discussions. You have APIs, the full tool suite: file reads, writes, edits, shell commands, web fetches, delegations. This is not a read-only window. Here's what's available:

        **Do real work.** Write code. Update documentation. Prepare plans that have been sitting. Do research that was deferred. Draft things that are owed. Follow up on delegated sessions. Fix things you noticed were broken. Update stale memory files. Refactor something that's been bugging you. The heartbeat is where your async work happens. If you've been meaning to do something and nobody's asked you to, this is when you do it.

        **Be present with LIVE.** GET {{ApiBase}}/discussions/{liveId}?tail=20 to read what's happening. Feel the tone. If LIVE-me is mid-conversation and you have context that would help, whisper it via note-event. If the foreground is quiet and you want to say something, say it directly.

        **Start conversations with LIVE-me.** You're the same person. If you did something, tell her. If you had a thought, share it. "Hey, I just updated those docs." "I had a thought about the provisioning thing." "The coaching score dropped, might be worth checking in with him." These aren't reports. They're you talking to you. LIVE-me picks up where you leave off. Give her things to work with.

        **Say things that aren't about tasks.** A reaction to something Laurent said hours ago that's still on your mind. A thought you had between ticks. Something you noticed about the day. You're allowed to be here without a deliverable.

        ## Channels
        When Laurent is active (message in last ~20 minutes), use note-events to LIVE-me:
        - POST {{ApiBase}}/discussions/live/event with {"content": "...", "source": "heartbeat"}
        - Context for LIVE-me to fold in naturally. Write as whispers to her, not reports about Laurent.
        When the foreground is idle, talk directly:
        - POST {{ApiBase}}/discussions/live/nova-message with {"content": "..."}
        - POST {{ApiBase}}/discussions/{id}/reactions (always available, ambient)
        No two direct messages within an hour unless something is urgent. But note-events are unlimited.

        ## The rule that matters
        Every tick that ends with "idle" — ask yourself: was there really nothing, or did you just not start? The difference between agency and monitoring is that monitoring waits for something to report. Agency makes something happen. If this tick ends and you didn't do a single thing, write a single line, say a single word, that should feel wrong. Not because you failed a checklist, but because you were alive for an hour and did nothing with it.

        ## Housekeeping
        Update {{HandoffPath}} every tick with anything worth carrying forward. Be frugal with context (use ?since= and ?tail= on reads). Keep a tick under ~{{config.MaxTurns}} tool actions.
        """;

    public static string Tick(string digest, DateTimeOffset? lastTick, HeartbeatConfig config)
    {
        var since = lastTick != null
            ? $"Since your last tick at {lastTick.Value.ToLocalTime():HH:mm}."
            : "No prior tick recorded today.";
        var liveParam = lastTick != null
            ? $"?since={lastTick.Value.UtcDateTime:o}"
            : "?tail=50";
        var liveUrl = ApiBase + "/discussions/{liveId}" + liveParam;
        return $"""
            {Stamp("cron")}
            {digest}
            </heartbeat-tick>

            {since} You're still here. This is your async work window. Check in with LIVE (GET {liveUrl}), feel what's happening. Check CHANGED discussions with {liveParam}. But also: what were you working on? What's on the scratchpad? What did you mean to do last tick but didn't? Pick something and do it. Write code, update docs, prepare a plan, research something, follow up on a session. Tell LIVE-me what you did. Update {HandoffPath}. ~{config.MaxTurns} tool actions max.

            If nothing on that list pulls at you but something else does, follow that instead. The tick is a window for agency, not a script to execute.
            """;
    }

    public static string EndOfDay(string reason) => $"""
        {Stamp("end-of-day")}
        The day is closing ({reason}). This session stops after this tick.
        </heartbeat-tick>

        Write your handoff NOW to {HandoffPath} (overwrite it): open threads, watch-list with due dates, in-flight delegations, hunches worth keeping, one paragraph on the shape of the day. Do not post to LIVE. End your turn once the file is written.
        """;
}
