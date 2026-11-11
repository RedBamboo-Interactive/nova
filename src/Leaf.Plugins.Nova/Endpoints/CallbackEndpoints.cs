using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// RedCompute session webhooks. These paths are on the kernel's auth BypassPaths list
/// (RedCompute posts without a bearer — manifest-level public-path declaration is a
/// known contract gap), so every handler here re-checks that the caller is loopback:
/// bypass skips the JWT, the old app's "local" auth mode is emulated in-endpoint.
/// </summary>
public static class CallbackEndpoints
{
    internal static async Task<IResult> HandleSessionCompleteAsync(
        HttpContext ctx, DiscussionStore store, EventInjector injector, LiveEvents live)
    {
        if (!IsLoopback(ctx))
            return Results.Json(new { error = "Local callers only" }, statusCode: 403);

        JsonElement body;
        try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
        catch { return Results.BadRequest(new { error = "invalid_body" }); }

        var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        string? callbackId = null;
        if (body.TryGetProperty("callbackId", out var callback))
        {
            if (callback.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(callback.GetString())
                || callback.GetString()!.Length > 200)
                return Results.BadRequest(new { error = "invalid_callback_id" });
            callbackId = callback.GetString();
        }
        var status = body.TryGetProperty("status", out var st) ? st.GetString() : null;
        var discussionId = ctx.Request.Query["discussionId"].ToString();

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(discussionId))
            return Results.BadRequest(new { error = "sessionId and discussionId are required" });

        var rawTitle = body.TryGetProperty("title", out var t) ? t.GetString() : null;
        // The title is written by the worker model and the ids come over HTTP: render them inert
        // and bounded. The event's metadata keeps the raw title.
        var title = NovaEventText.Field(rawTitle, MaxTitle);
        var shownSession = NovaEventText.Field(sessionId, MaxIdentity);
        var stopReason = body.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
        var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var deliveredMessageUid = body.TryGetProperty("deliveredMessageUid", out var delivered)
            && delivered.ValueKind == JsonValueKind.String ? delivered.GetString() : null;
        var promptMessageUid = body.TryGetProperty("promptMessageUid", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() : null;
        var executionObserved = body.TryGetProperty("executionObserved", out var observed) && observed.ValueKind == JsonValueKind.True;
        // A turn that ended while its background work still runs: RedCompute keeps the registration
        // and reports again when that work wakes the session, with the next deliverySequence.
        var backgroundTasksRunning = body.TryGetProperty("backgroundTasksRunning", out var background)
            && background.ValueKind == JsonValueKind.Number && background.TryGetInt32(out var running) ? running : 0;
        var deliverySequence = body.TryGetProperty("deliverySequence", out var sequence)
            && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt32(out var parsedSequence) ? parsedSequence : (int?)null;
        // RedCompute derives what the session is doing; Nova only reads it.
        var workState = body.TryGetProperty("workState", out var work) && work.ValueKind == JsonValueKind.String ? work.GetString() : null;
        // Inputs still queued on the session when RedCompute built this payload (absent from older senders).
        var pendingInputs = body.TryGetProperty("pendingInputs", out var pendingValue)
            && pendingValue.ValueKind == JsonValueKind.Number && pendingValue.TryGetInt32(out var pendingCount) && pendingCount > 0 ? pendingCount : 0;
        // RedCompute's turn ledger: the turn this completion reports (shared by every prompt one turn
        // carried, steered ones included), how it ended, and the interrupt that ended it. Absent from
        // older senders.
        var turnId = body.TryGetProperty("turnId", out var turnValue) && turnValue.ValueKind == JsonValueKind.String
            ? turnValue.GetString() : null;
        var turnOutcome = body.TryGetProperty("turnOutcome", out var turnOutcomeValue) && turnOutcomeValue.ValueKind == JsonValueKind.String
            ? turnOutcomeValue.GetString() : null;
        string? interruptSource = null, interruptedByMessageUid = null;
        if (body.TryGetProperty("interruptedBy", out var interruptedBy) && interruptedBy.ValueKind == JsonValueKind.Object)
        {
            interruptSource = interruptedBy.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null;
            interruptedByMessageUid = interruptedBy.TryGetProperty("messageUid", out var by) && by.ValueKind == JsonValueKind.String ? by.GetString() : null;
        }
        var backgroundTurn = status == "Idle" && workState == "background";
        // A turn the session started without a queued prompt (a peer message, a self-wake), reported
        // through the discussion's session-scoped question subscription: no prompt of ours ran it.
        var unprompted = body.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.String
            && trigger.GetString() == "unprompted";
        var provenEndedCompletion = status == "Ended" && reason == "completed"
            && promptMessageUid is not null && deliveredMessageUid is not null && executionObserved;

        var resumablePause = status is "Stopped" or "Ended"
            && DiscussionStatus.IsAutoResumableStop(stopReason);
        var idleParking = stopReason is "idle_timeout" or "idle_ttl_expired";
        // RedCompute continues a delegated prompt a restart cut, by itself, once it is back
        // (RestartContinuations): the supervisor must not write that continuation by hand.
        var autoContinue = resumablePause && !idleParking
            && body.TryGetProperty("autoContinue", out var continues) && continues.ValueKind == JsonValueKind.True;
        // The completion of a continuation RedCompute sent after a restart.
        var restartContinuation = body.TryGetProperty("trigger", out var triggerValue) && triggerValue.ValueKind == JsonValueKind.String
            && triggerValue.GetString() == "restart-continuation";
        // Background turns RedCompute absorbed since its last report (at most one report per window).
        var coalescedTurns = body.TryGetProperty("coalescedTurns", out var coalesced)
            && coalesced.ValueKind == JsonValueKind.Number && coalesced.TryGetInt32(out var absorbed) && absorbed > 0 ? absorbed : 0;
        var pauseSuffix = !resumablePause ? ""
            : idleParking ? " — parked; resumes on the next message"
            : autoContinue ? " — RedCompute resumes it after the restart"
            : " — waiting to resume after restart";
        // An interrupted turn did not complete, whatever status the session went idle with.
        var interruptedTurn = turnOutcome == "interrupted" && status is "Idle" or "Ended"
            && !resumablePause && stopReason != "usage_limit";
        var outcome = resumablePause || stopReason == "usage_limit" ? "paused"
            : interruptedTurn ? "interrupted"
            : backgroundTurn ? "background"
            : status == "Idle" || provenEndedCompletion ? "completed"
            : status == "Cancelled" ? reason == "superseded" ? "superseded" : "cancelled"
            : status == "Error" || reason == "failed" ? "failed"
            : status == "Ended" ? "ended"
            : status == "Stopped" ? "stopped" : "status";
        var unpromptedTurn = unprompted && outcome == "completed";
        // "Completed" alone reads as "all done" while the session still has prompts to run.
        var queuedNext = outcome == "completed" && pendingInputs > 0;

        var summary = (status, stopReason) switch
        {
            _ when idleParking => $"Session {shownSession} parked after inactivity; it will resume on the next message",
            _ when autoContinue => $"Session {shownSession} paused for restart; RedCompute resumes it with a continuation once it is back, and its completion will follow. Don't send a continuation yourself",
            _ when resumablePause => $"Session {shownSession} paused for restart; awaiting recovery, completion has not been reported",
            (_, "usage_limit") => $"Session {shownSession} paused — usage limit reached{(title != null ? $" ({title})" : "")}",
            _ when interruptedTurn => $"Session {shownSession} turn was interrupted{InterruptedBy(interruptSource, interruptedByMessageUid)}{(title != null ? $" ({title})" : "")}; it did not complete"
                + (backgroundTurn ? $", and {BackgroundTasks(backgroundTasksRunning)} still running" : "")
                + (pendingInputs > 0 ? $"; it has {QueuedPrompts(pendingInputs)}, which it will run next" : ""),
            _ when backgroundTurn => $"Session {shownSession} ended its turn{(title != null ? $" ({title})" : "")} with {BackgroundTasks(backgroundTasksRunning)} still running; it is not finished and will report again when that work wakes it",
            _ when unpromptedTurn => $"Session {shownSession} finished a turn it started on its own{(title != null ? $" ({title})" : "")}"
                + (pendingInputs > 0 ? $"; it has {QueuedPrompts(pendingInputs)}, which it will run next" : ""),
            _ when queuedNext => $"Session {shownSession} completed this turn{(title != null ? $" ({title})" : "")} and has {QueuedPrompts(pendingInputs)}, which it will run next",
            _ when provenEndedCompletion => $"Session {shownSession} completed{(title != null ? $": {title}" : "")}",
            ("Idle", _) => $"Session {shownSession} completed{(title != null ? $": {title}" : "")}",
            ("Cancelled", _) => $"Session {shownSession} input was {(reason == "superseded" ? "superseded" : "cancelled")}; it did not complete",
            ("Stopped", _) => $"Session {shownSession} was stopped",
            ("Error" or "Ended", _) => $"Session {shownSession} ended with status: {NovaEventText.Field(status, MaxIdentity)}",
            _ => $"Session {shownSession} status: {NovaEventText.Field(status, MaxIdentity)}",
        };
        if (unprompted && !unpromptedTurn) summary += " (in a turn it started on its own)";
        if (restartContinuation) summary += " (after RedCompute resumed it from a restart)";
        if (coalescedTurns > 0) summary += $" ({coalescedTurns} more background {(coalescedTurns == 1 ? "turn" : "turns")} since the last report)";

        var eventKind = unpromptedTurn ? "unprompted-turn"
            : outcome switch { "paused" => "paused", "background" => "turn-ended", "interrupted" => "interrupted", _ => "complete" };
        var eventContent = interruptedTurn
            ? $"""
            <nova-event source="callback:session-{eventKind}" type="session-{eventKind}" stopReason="{NovaEventText.Attribute(stopReason ?? "unknown")}" interruptSource="{NovaEventText.Attribute(interruptSource ?? "unknown")}">
            {summary}
            </nova-event>
            """
            : backgroundTurn
            ? $"""
            <nova-event source="callback:session-{eventKind}" type="session-{eventKind}" stopReason="{NovaEventText.Attribute(stopReason ?? "unknown")}" backgroundTasksRunning="{backgroundTasksRunning}">
            {summary}
            </nova-event>
            """
            : $"""
            <nova-event source="callback:session-{eventKind}" type="session-{eventKind}" stopReason="{NovaEventText.Attribute(stopReason ?? "unknown")}">
            {summary}
            </nova-event>
            """;

        var discussion = await store.GetAsync(discussionId);
        if (discussion is null)
            return Results.Json(new { error = "event_injection_failed", message = "Discussion not found" }, statusCode: 502);

        // One provider turn can resolve several registrations: RedCompute merges queued prompts into
        // one delivery, and each prompt's registration reports that same turn under its own
        // callbackId. A delivered turn is therefore keyed on the turn itself, which RedCompute
        // reports identically to all of them (deliveredMessageUid, the merged batch's first
        // prompt, plus the deliverySequence below for a turn woken by background work). That holds
        // whether or not activity was observed: a background turn that never woke reports on
        // settle without it, and so can a member whose earlier delivery was still retrying.
        // A later turn of the same session has a new deliveredMessageUid and still arrives.
        // Pause, cancellation and supersession are per-prompt outcomes and keep the callbackId key.
        // RedCompute now names the turn itself (turnId): a steered prompt reports the turn it joined
        // under that turn's id, while its deliveredMessageUid is its own, so the two completions of
        // one turn collapse into one event. Older senders fall back to deliveredMessageUid, which
        // is the turn id of every unsteered turn.
        var turnKey = turnId is { Length: > 0 and <= MaxIdentity } ? turnId : deliveredMessageUid;
        var turnIdentity = callbackId is not null
            && turnKey is { Length: > 0 and <= MaxIdentity }
            && outcome is not ("paused" or "cancelled" or "superseded");
        // Legacy senders remain compatible. A session ID alone cannot identify a
        // completion: later continuations of that same session must still arrive.
        var key = turnIdentity ? $"session-turn:{sessionId}:{discussionId}:{turnKey}"
            : callbackId is null ? null : $"session-complete:{sessionId}:{discussionId}:{callbackId}";
        // Pause and eventual terminal delivery share prompt correlation, but not
        // deduplication identity. A pause must never swallow the later completion.
        if (key is not null && outcome == "paused") key += $":paused:{stopReason}";
        // Each turn reported under one registration carries its own sequence; retries repeat it.
        if (key is not null && deliverySequence > 1) key += $":turn:{deliverySequence}";
        var metadata = JsonSerializer.SerializeToElement(new {
            sessionId, callbackId, promptMessageUid, status, stopReason, reason, deliveredMessageUid, executionObserved, outcome,
            backgroundTasksRunning, deliverySequence, workState, pendingInputs, title = rawTitle,
            trigger = unprompted ? "unprompted" : restartContinuation ? "restart-continuation" : null,
            autoContinue = autoContinue ? true : (bool?)null,
            coalescedTurns = coalescedTurns > 0 ? coalescedTurns : (int?)null,
            turnId, turnOutcome,
            interruptedBy = interruptSource is null && interruptedByMessageUid is null ? null
                : new { source = interruptSource, messageUid = interruptedByMessageUid },
        });
        var forwarded = await injector.InjectAsync(discussion, eventContent, null, $"delegate:{sessionId}",
            metadata: metadata,
            idempotencyKey: key, redeliverOnReuse: key is not null,
            deliveryIdempotencyKey: key, ct: ctx.RequestAborted, framed: true);
        // Do not introduce extra unkeyed retries from an older sender. Recovery
        // is safe only when the callback carries its stable delivery identity.
        if (key is not null && !forwarded && discussion.SessionId is not null)
            return Results.Json(new { error = "event_delivery_failed" }, statusCode: 502);

        if (!discussion.Confidential)
            await live.PostForOwnerAsync(discussion, "callback", interruptedTurn
                    ? $"Delegated session turn was interrupted{InterruptedBy(interruptSource, interruptedByMessageUid)}{(title != null ? $": {title}" : "")}"
                    : backgroundTurn
                    ? $"Delegated session turn ended with {BackgroundTasks(backgroundTasksRunning)} still running{(title != null ? $": {title}" : "")}"
                    : unpromptedTurn
                    ? $"Delegated session finished a turn it started on its own{(title != null ? $": {title}" : "")}"
                    : queuedNext
                    ? $"Delegated session completed this turn and has {QueuedPrompts(pendingInputs)}, which it will run next{(title != null ? $": {title}" : "")}"
                    : $"Delegated session {outcome}{(title != null ? $": {title}" : "")}{pauseSuffix}",
                idempotencyKey: key is null ? null : $"{key}:live", ct: ctx.RequestAborted);
        return Results.Ok(new { handled = true, sessionId, discussionId, status });
    }

    private static string BackgroundTasks(int count) => count == 1 ? "1 background task" : $"{count} background tasks";

    /// <summary>Who interrupted the turn, in the words of the event; the message UID is rendered inert.</summary>
    internal static string InterruptedBy(string? source, string? messageUid)
    {
        var prompt = NovaEventText.Field(messageUid, MaxIdentity);
        return source switch
        {
            "interrupt-current" => prompt is null ? " by an interrupt-current prompt" : $" by prompt {prompt} (interrupt-current)",
            "send-now" => prompt is null ? " by send-now" : $" by send-now for prompt {prompt}",
            "interrupt" => " by a stop request",
            _ => "",
        };
    }
    private static string QueuedPrompts(int count) => count == 1 ? "1 more queued prompt" : $"{count} more queued prompts";

    // Bounds for worker-authored values rendered into an event; metadata keeps them whole.
    internal const int MaxTitle = 200;
    internal const int MaxIdentity = 200;
    internal const int MaxHeader = 80;
    internal const int MaxQuestion = 1000;
    internal const int MaxLabel = 200;
    internal const int MaxDescription = 500;
    internal const int MaxQuestionSection = 6000;
    internal const int MaxToolName = 120;
    internal const int MaxSummary = 1000;
    internal const int MaxLiveSummary = 160;

    /// <summary>
    /// A delegated session parked on a structured question (Claude AskUserQuestion, Codex
    /// requestUserInput). RedCompute posts here once per requestId when the delegation opted in
    /// with questionUrl. The discussion receives everything needed to answer, including the exact
    /// answer contract, so the worker is not left waiting for its timeout. The later completion
    /// callback is independent and still arrives on its own route.
    /// <para>
    /// <c>kind: "permission"</c> is a tool call waiting for a person's approval, not a question.
    /// RedCompute takes that decision only from a person (an Agent's token gets 403
    /// approval_requires_person), so the discussion is told it is its person's to give, with the
    /// tool and what it would do, and no answer contract; LIVE tells the person.
    /// </para>
    /// </summary>
    internal static async Task<IResult> HandleSessionQuestionAsync(
        HttpContext ctx, DiscussionStore store, EventInjector injector, LiveEvents live)
    {
        if (!IsLoopback(ctx))
            return Results.Json(new { error = "Local callers only" }, statusCode: 403);

        JsonElement body;
        try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
        catch { return Results.BadRequest(new { error = "invalid_body" }); }

        var sessionId = Text(body, "sessionId");
        var requestId = Text(body, "requestId");
        var discussionId = ctx.Request.Query["discussionId"].ToString();
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(discussionId))
            return Results.BadRequest(new { error = "sessionId, requestId and discussionId are required" });
        // The session id builds the answer URL, so it must be a real session id; the request id is
        // provider-chosen and only ever rendered escaped.
        if (!DelegationActivity.ValidSessionId(sessionId) || requestId.Length > MaxIdentity)
            return Results.BadRequest(new { error = "invalid_identity" });

        var discussion = await store.GetAsync(discussionId);
        if (discussion is null)
            return Results.Json(new { error = "event_injection_failed", message = "Discussion not found" }, statusCode: 502);

        var title = Text(body, "title");
        var expiresAt = Text(body, "expiresAt");
        // One delivery per parked question, whichever registration or retry carried it.
        var key = $"session-question:{sessionId}:{requestId}";
        string eventContent;
        JsonElement metadata;
        string liveLine;
        if (IsPermission(body))
        {
            var permission = body.TryGetProperty("permission", out var p) && p.ValueKind == JsonValueKind.Object
                ? p : default;
            var toolName = Text(permission, "toolName") ?? Text(body, "toolName");
            var toolUseId = Text(permission, "toolUseId") ?? Text(body, "toolUseId");
            var summary = Text(permission, "summary");
            var cwd = Text(permission, "cwd");
            eventContent = SessionPermissionEvent(sessionId, title, requestId, toolName, summary, cwd, expiresAt);
            // No answer URL: there is nothing here an Agent may post. The tool input stays out
            // too; the summary says what the call does, and the session holds the rest.
            metadata = JsonSerializer.SerializeToElement(new
            {
                sessionId, requestId, title, kind = "permission",
                toolName, toolUseId,
                permission = new { toolName, toolUseId, summary, cwd },
                askedAt = Text(body, "askedAt"), expiresAt,
                decidedBy = "person",
                outcome = "permission",
            });
            liveLine = PermissionLiveLine(title, toolName, summary);
        }
        else
        {
            var questions = body.TryGetProperty("questions", out var q) && q.ValueKind == JsonValueKind.Array
                ? q : JsonSerializer.SerializeToElement(Array.Empty<object>());
            var answerUrl = $"http://127.0.0.1:18800/ai-session/sessions/{sessionId}/question";
            eventContent = SessionQuestionEvent(sessionId, title, requestId, questions, expiresAt, answerUrl);
            metadata = JsonSerializer.SerializeToElement(new
            {
                sessionId, requestId, title,
                toolName = Text(body, "toolName"), toolUseId = Text(body, "toolUseId"),
                questions, askedAt = Text(body, "askedAt"), expiresAt,
                answer = new { method = "POST", url = answerUrl },
                outcome = "question",
            });
            var shownTitle = NovaEventText.Field(title, MaxTitle);
            liveLine = $"Delegated session is waiting on a question{(shownTitle != null ? $": {shownTitle}" : "")}";
        }
        var forwarded = await injector.InjectAsync(discussion, eventContent, null, $"delegate:{sessionId}",
            metadata: metadata,
            idempotencyKey: key, redeliverOnReuse: true,
            deliveryIdempotencyKey: key, ct: ctx.RequestAborted, framed: true);
        if (!forwarded && discussion.SessionId is not null)
            return Results.Json(new { error = "event_delivery_failed" }, statusCode: 502);

        if (!discussion.Confidential)
            await live.PostForOwnerAsync(discussion, "callback", liveLine, idempotencyKey: $"{key}:live", ct: ctx.RequestAborted);
        return Results.Ok(new { handled = true, sessionId, discussionId, requestId });
    }

    /// <summary>RedCompute's <c>kind</c>: absent or <c>question</c> is an ordinary question.</summary>
    internal static bool IsPermission(JsonElement body)
        => string.Equals(Text(body, "kind"), "permission", StringComparison.Ordinal);

    /// <summary>
    /// The model-facing event for a delegated tool call waiting for approval. It says plainly that
    /// the decision is the discussion's person's, that an Agent's decision is refused, and what
    /// the call would do; it carries no answer contract. Worker-authored values go through
    /// <see cref="NovaEventText"/> like a question's.
    /// </summary>
    internal static string SessionPermissionEvent(string sessionId, string? title, string requestId,
        string? toolName, string? summary, string? cwd, string? expiresAt)
    {
        var shownTitle = NovaEventText.Field(title, MaxTitle);
        var shownTool = NovaEventText.Field(toolName, MaxToolName) ?? "a tool";
        var shownSummary = NovaEventText.Field(summary, MaxSummary);
        var shownCwd = NovaEventText.Field(cwd, MaxDescription);
        var shownExpiry = NovaEventText.Field(expiresAt, MaxIdentity);
        var text = new System.Text.StringBuilder();
        text.Append($"Delegated session {sessionId}{(shownTitle != null ? $" ({shownTitle})" : "")} is waiting for a person's approval before it runs {shownTool}");
        text.AppendLine(shownExpiry != null
            ? $", until {shownExpiry}. If nobody decides by then, the call is denied."
            : ".");
        text.AppendLine($"Tool: {shownTool}");
        if (shownSummary != null) text.AppendLine($"What it would do: {shownSummary}");
        if (shownCwd != null) text.AppendLine($"Working directory: {shownCwd}");
        text.AppendLine();
        text.AppendLine("This approval is not yours to give. It belongs to the person who owns this discussion, and only they can decide it, with the Allow or Deny buttons on the approval card in that session's chat.");
        text.AppendLine("You cannot answer it: RedCompute refuses an Agent's decision (your execution token gets 403 approval_requires_person), so do not post one and do not retry.");
        text.Append("Tell them plainly what is waiting and why it matters, then let them decide. The session carries on by itself once they do.");
        return $"""
            <nova-event source="callback:session-question" type="session-permission" sessionId="{NovaEventText.Attribute(sessionId)}" requestId="{NovaEventText.Attribute(requestId)}" expiresAt="{NovaEventText.Attribute(expiresAt ?? "none")}">
            {text}
            </nova-event>
            """;
    }

    /// <summary>The LIVE line the person reads: whose approval, which session, the tool and what it would do.</summary>
    internal static string PermissionLiveLine(string? title, string? toolName, string? summary)
    {
        var shownTitle = NovaEventText.Field(title, MaxTitle);
        var shownTool = NovaEventText.Field(toolName, MaxToolName) ?? "a tool";
        var shownSummary = NovaEventText.Field(summary, MaxLiveSummary);
        var where = shownTitle != null ? $" in delegated session {shownTitle}" : " in a delegated session";
        return $"Waiting for your approval{where}: {shownTool}{(shownSummary != null && shownSummary != shownTool ? $" {shownSummary}" : "")}";
    }

    /// <summary>
    /// The model-facing question event. Everything the worker wrote (title, headers, questions,
    /// option labels and descriptions) is rendered through <see cref="NovaEventText"/>: frame tags
    /// neutralized, one line each, bounded per field, and the whole question section bounded. The
    /// fixed answer contract follows outside that bound, so it is always complete.
    /// </summary>
    internal static string SessionQuestionEvent(string sessionId, string? title, string requestId,
        JsonElement questions, string? expiresAt, string answerUrl)
    {
        var shownTitle = NovaEventText.Field(title, MaxTitle);
        var shownExpiry = NovaEventText.Field(expiresAt, MaxIdentity);
        var text = new System.Text.StringBuilder();
        text.Append($"Delegated session {sessionId}{(shownTitle != null ? $" ({shownTitle})" : "")} is blocked on a question and waits for an answer");
        text.AppendLine(shownExpiry != null
            ? $" until {shownExpiry}. If nobody answers by then it proceeds with its own best judgement."
            : ". It has no deadline: it waits until answered or stopped.");
        var section = new NovaEventText.Bounded(MaxQuestionSection);
        var index = 0;
        foreach (var question in questions.EnumerateArray())
        {
            index++;
            var header = NovaEventText.Field(Text(question, "header"), MaxHeader);
            var multi = question.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            section.AppendLine();
            section.AppendLine($"Question {index}{(header != null ? $" [{header}]" : "")}{(multi ? " (multi-select)" : "")}: {NovaEventText.Field(Text(question, "question"), MaxQuestion)}");
            if (question.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                foreach (var option in options.EnumerateArray())
                {
                    var description = NovaEventText.Field(Text(option, "description"), MaxDescription);
                    section.AppendLine($"- {NovaEventText.Field(Text(option, "label"), MaxLabel)}{(description is null ? "" : $": {description}")}");
                }
        }
        text.Append(section.ToString().TrimEnd('\n')).AppendLine();
        text.AppendLine();
        text.AppendLine($"Answer contract: POST {answerUrl} with your execution bearer and a JSON body carrying \"requestId\": {JsonSerializer.Serialize(NovaEventText.Field(requestId, MaxIdentity))} plus one of:");
        text.AppendLine("- \"answers\": an object keyed by the exact question text, valued with the chosen option label (multi-select: labels comma-separated in one string), or an array of those values in question order;");
        text.AppendLine("- \"response\": free text in your own words instead of picking options;");
        text.AppendLine("- \"decline\": true (optional \"reason\") to dismiss the question.");
        text.AppendLine("Send answers or response, not both: response outranks answers (it replaces every selection), so RedCompute forwards response only when no answers are given.");
        text.Append("409 request_not_pending means it was already answered, expired or cancelled.");
        return $"""
            <nova-event source="callback:session-question" type="session-question" sessionId="{NovaEventText.Attribute(sessionId)}" requestId="{NovaEventText.Attribute(requestId)}" expiresAt="{NovaEventText.Attribute(expiresAt ?? "none")}">
            {text}
            </nova-event>
            """;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/callbacks/session-complete", HandleSessionCompleteAsync)
            .WithSummary("RedCompute webhook reporting that a delegated session finished a turn, injected into the discussion named by the discussionId query parameter. Loopback callers only and not meant for Agents; body needs sessionId.");
        group.MapPost("/callbacks/session-question", HandleSessionQuestionAsync)
            .WithSummary("RedCompute webhook reporting that a delegated session is blocked on a question, injected into the discussion named by the discussionId query parameter. Loopback callers only and not meant for Agents; body needs sessionId and requestId.");

        group.MapPost("/callbacks/agent-response", async (HttpContext ctx, DiscussionStore store, EventInjector injector, RedComputeClient redCompute) =>
        {
            if (!IsLoopback(ctx))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }

            var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var replyToDiscussionId = ctx.Request.Query["replyTo"].ToString();
            var respondingAgentId = ctx.Request.Query["agentId"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(replyToDiscussionId))
                return Results.BadRequest(new { error = "sessionId and replyTo are required" });

            // Small delay to ensure messages are persisted before we read them.
            await Task.Delay(1000);

            string responseContent;
            try
            {
                using var raw = await redCompute.GetSessionRawAsync(sessionId)
                    ?? throw new InvalidOperationException("session fetch failed");
                var messages = raw.RootElement.GetProperty("messages").EnumerateArray().ToList();

                var lastUserIdx = -1;
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].GetProperty("role").GetString() == "user") { lastUserIdx = i; break; }
                }

                var responseParts = new List<string>();
                for (int i = lastUserIdx + 1; i < messages.Count; i++)
                {
                    var m = messages[i];
                    if (m.GetProperty("role").GetString() == "assistant"
                        && m.GetProperty("eventType").GetString() == "text"
                        && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var text = c.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            responseParts.Add(text!);
                    }
                }

                responseContent = string.Join("", responseParts);
                if (string.IsNullOrWhiteSpace(responseContent))
                    responseContent = "(no response)";
            }
            catch (Exception ex)
            {
                responseContent = $"(failed to fetch response: {ex.Message})";
            }

            var discussion = await store.GetAsync(replyToDiscussionId);
            if (discussion is null)
                return Results.Json(new { error = "delivery_failed", message = "Target discussion not found" }, statusCode: 502);

            await injector.InjectAsync(discussion, responseContent, null, $"agent-response:{sessionId}",
                senderAgentId: string.IsNullOrEmpty(respondingAgentId) ? null : respondingAgentId);

            // Stop the responding session (may already be stopped).
            try { await redCompute.StopAsync(sessionId); }
            catch { }

            return Results.Ok(new { handled = true, sessionId, replyToDiscussionId, respondingAgentId });
        })
            .WithSummary("RedCompute webhook that copies the last reply of an Agent session into the discussion named by the replyTo query parameter, then stops that session. Loopback callers only and not meant for Agents; body needs sessionId.");

        group.MapPost("/callbacks/external-conversation", async (
            HttpContext ctx, ExternalAgentConversationProvider provider) =>
        {
            if (!IsLoopback(ctx))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }
            var sessionId = body.TryGetProperty("sessionId", out var session)
                            && session.ValueKind == JsonValueKind.String
                ? session.GetString() : null;
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            await provider.NotifySettledAsync(sessionId, ctx.RequestAborted);
            return Results.Ok(new { handled = true, sessionId });
        })
            .WithSummary("RedCompute webhook telling Nova that an external Agent conversation session has settled. Loopback callers only and not meant for Agents; body needs sessionId.");
    }

    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip != null && System.Net.IPAddress.IsLoopback(ip);
    }
}
