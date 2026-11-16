using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// The async jobs a discussion launched (images, video, music, workflows) for the chat's job cards.
/// <para>
/// Authorized like history-page, and then each job is checked here as well, because the Compute
/// gateway trusts Nova: a job belongs to the discussion only when its creation provenance names
/// this discussion, any session it names is this discussion's own session (a delegated worker
/// names the parent discussion and its own session, so its jobs stay on its own card), it was
/// made for this discussion's owner, and it is not confidential unless the discussion is.
/// </para>
/// <para>
/// Every list is bounded below: <c>since</c> is required and clamped to the discussion's creation
/// and to 30 days back. A single job is re-read with <c>GET /jobs/{id}</c>, never by re-listing.
/// Outputs are served through this discussion's own route, so the browser never calls RedCompute.
/// </para>
/// </summary>
public static class DiscussionJobEndpoints
{
    /// <summary>Kinds a card never shows: the session itself, speech in and out.</summary>
    internal static readonly HashSet<string> ExcludedCapabilities =
        new(["ai-session", "tts", "stt"], StringComparer.OrdinalIgnoreCase);

    internal static readonly TimeSpan MaxLookback = TimeSpan.FromDays(30);
    internal static readonly TimeSpan CreationSlack = TimeSpan.FromMinutes(1);
    internal const int PageSize = 200;
    internal const int MaxPages = 3;
    internal const int DetailConcurrency = 4;
    /// <summary>Same as the shared rule: a success this quick never gets a card, so it needs no detail read.</summary>
    internal const long QuickSuccessMs = 2000;

    private static readonly string[] ProjectedFields =
    [
        "id", "capability", "providerName", "status", "queuedAt", "startedAt", "completedAt",
        "durationMs", "progress", "stage", "terminal", "name", "errorMessage", "parentJobId", "confidential",
    ];

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussions/{id}/jobs", (string id, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute)
                => ListAsync(id, ctx, store, redCompute, DateTimeOffset.UtcNow))
            .WithName("NovaDiscussionJobs")
            .WithSummary("Async jobs the discussion launched, for its job cards")
            .WithDescription("Images, video, music and workflows this discussion's own turns started, queued in [since, until). Authorized like history-page, then filtered to jobs whose provenance names this discussion and its own session, made for its owner, and confidential only when the discussion is. AI sessions, text to speech and speech to text are left out. `since` is required (ISO 8601) and clamped to the discussion's creation and 30 days back; `until` is optional. Each item carries progress, stage, and outputs rewritten to this discussion's output route.");

        group.MapGet("/discussions/{id}/jobs/{jobId:guid}", (string id, Guid jobId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute)
                => GetAsync(id, jobId, ctx, store, redCompute))
            .WithName("NovaDiscussionJob")
            .WithSummary("One job of the discussion, re-read")
            .WithDescription("The same projection as one item of /jobs, read with RedCompute's GET /jobs/{id}. 404 job_not_found when the job is gone or is not this discussion's.");

        group.MapGet("/discussions/{id}/jobs/{jobId:guid}/output", (string id, Guid jobId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute)
                => OutputAsync(id, jobId, ctx, store, redCompute))
            .WithName("NovaDiscussionJobOutput")
            .WithSummary("One output of a discussion's job")
            .WithDescription("Streams outputs[index] (default 0) of a job that belongs to this discussion. Supports HTTP byte ranges.");

        group.MapPost("/discussions/{id}/jobs/{jobId:guid}/cancel", (string id, Guid jobId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute)
                => CancelAsync(id, jobId, ctx, store, redCompute))
            .WithName("NovaDiscussionJobCancel")
            .WithSummary("Stops one of the discussion's running jobs")
            .WithDescription("A person's action from the job card. Cancels through RedCompute's own authorized DELETE /jobs/{id}, as the person. 409 job_finished when the job already ended; RedCompute's refusal comes back unchanged.");
    }

    internal static async Task<IResult> ListAsync(string id, HttpContext ctx, DiscussionStore store,
        RedComputeClient redCompute, DateTimeOffset now)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return DiscussionEndpoints.NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);

        var query = ctx.Request.Query;
        if (!TryParseInstant(query["since"].FirstOrDefault(), out var since))
            return Error(400, "invalid_since", "since is required: an ISO 8601 instant. An unbounded job list is refused.");
        DateTimeOffset? until = null;
        if (query["until"].FirstOrDefault() is { Length: > 0 } rawUntil)
        {
            if (!TryParseInstant(rawUntil, out var parsed))
                return Error(400, "invalid_until", "until must be an ISO 8601 instant");
            if (parsed <= since)
                return Error(400, "invalid_range", "until must be later than since");
            until = parsed;
        }

        var floor = Max(Utc(discussion.CreatedAt) - CreationSlack, now - MaxLookback);
        var from = Max(since, floor);
        if (until is { } end && end <= from)
            return Page([], from);

        var summaries = new List<JsonObject>();
        for (var page = 0; page < MaxPages; page++)
        {
            RedComputeClient.ProxyResult result;
            try
            {
                result = await redCompute.ListContextJobsAsync("discussion", discussion.Id, from, until,
                    PageSize, page * PageSize, ctx.RequestAborted);
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Error(503, "redcompute_unavailable", ex.Message);
            }
            if (result.StatusCode != 200) return Upstream(result, "list");
            if (ReadItems(result.Content) is not { } items)
                return Error(502, "invalid_jobs_page", "RedCompute returned an invalid jobs page");
            summaries.AddRange(items.Where(item => Belongs(item, discussion)));
            if (items.Count < PageSize) break;
        }

        var projected = new JsonObject?[summaries.Count];
        using var gate = new SemaphoreSlim(DetailConcurrency);
        await Task.WhenAll(summaries.Select(async (summary, index) =>
        {
            if (IsQuickSuccess(summary) || IdOf(summary) is not { } jobId)
            {
                projected[index] = Project(summary, discussion.Id);
                return;
            }
            await gate.WaitAsync(ctx.RequestAborted);
            try
            {
                var detail = await ReadDetailAsync(redCompute, jobId, ctx.RequestAborted);
                projected[index] = detail switch
                {
                    { Gone: true } => null,
                    { Job: { } job } when Belongs(job, discussion) => Project(job, discussion.Id),
                    { Job: not null } => null,
                    // A detail read that failed keeps the summary: the card shows less, not nothing.
                    _ => Project(summary, discussion.Id),
                };
            }
            finally { gate.Release(); }
        }));

        return Page(projected.OfType<JsonObject>(), from);
    }

    internal static async Task<IResult> GetAsync(string id, Guid jobId, HttpContext ctx, DiscussionStore store,
        RedComputeClient redCompute)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return DiscussionEndpoints.NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);

        var (job, failure) = await ReadOwnJobAsync(discussion, jobId, redCompute, ctx.RequestAborted);
        return failure ?? Results.Content(Project(job!, discussion.Id).ToJsonString(), "application/json");
    }

    internal static async Task<IResult> OutputAsync(string id, Guid jobId, HttpContext ctx, DiscussionStore store,
        RedComputeClient redCompute)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return DiscussionEndpoints.NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);

        var rawIndex = ctx.Request.Query["index"].FirstOrDefault();
        var index = 0;
        if (rawIndex is { Length: > 0 } && (!int.TryParse(rawIndex, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index > 999))
            return Error(400, "invalid_index", "index must be a small non-negative integer");

        var (job, failure) = await ReadOwnJobAsync(discussion, jobId, redCompute, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (job!["outputs"] is not JsonArray outputs || index >= outputs.Count
            || outputs[index] is not JsonObject output
            || output["url"] is not JsonValue urlValue || !urlValue.TryGetValue<string>(out var path))
            return Error(404, "output_not_found", "The job has no such output");

        HttpResponseMessage upstream;
        try
        {
            upstream = await redCompute.OpenJobOutputAsync(jobId, path, ctx.Request.Headers.Range.ToString(), ctx.RequestAborted);
        }
        catch (ArgumentException)
        {
            return Error(502, "invalid_job_output", "RedCompute named an output outside this job's output route");
        }
        catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return Error(503, "redcompute_unavailable", ex.Message);
        }

        using (upstream)
        {
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();
            if (upstream.Content.Headers.ContentLength is { } contentLength)
                ctx.Response.ContentLength = contentLength;
            if (upstream.Content.Headers.ContentRange is { } contentRange)
                ctx.Response.Headers.ContentRange = contentRange.ToString();
            if (upstream.Headers.AcceptRanges.Count > 0)
                ctx.Response.Headers.AcceptRanges = string.Join(", ", upstream.Headers.AcceptRanges);
            if (upstream.Headers.ETag is { } etag)
                ctx.Response.Headers.ETag = etag.ToString();
            if (upstream.Content.Headers.LastModified is { } lastModified)
                ctx.Response.Headers.LastModified = lastModified.ToString("R");
            // Outputs of a finished job do not change; the browser may keep them for this session.
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        return Results.Empty;
    }

    internal static async Task<IResult> CancelAsync(string id, Guid jobId, HttpContext ctx, DiscussionStore store,
        RedComputeClient redCompute)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return DiscussionEndpoints.NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);
        if (!DiscussionAccessPolicy.IsPersonAtBrowser(ctx.User))
            return Error(403, "cancel_requires_person", "Stopping a job from its card is a person's action in a browser.");

        var (job, failure) = await ReadOwnJobAsync(discussion, jobId, redCompute, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (job!["terminal"] is JsonValue terminal && terminal.TryGetValue<bool>(out var ended) && ended)
            return Error(409, "job_finished", "The job already ended");

        RedComputeClient.ProxyResult result;
        try { result = await redCompute.CancelJobAsync(jobId, ctx.RequestAborted); }
        catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return Error(503, "redcompute_unavailable", ex.Message);
        }
        if (result.StatusCode is >= 200 and < 300)
            return Results.Json(new { success = true, jobId });
        return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
    }

    /// <summary>Whether a job record, summary or detail, is one of this discussion's own.</summary>
    internal static bool Belongs(JsonObject job, DiscussionRead discussion)
    {
        if (ExcludedCapabilities.Contains(Text(job["capability"]) ?? "")) return false;
        if (Bool(job["confidential"]) && !discussion.Confidential) return false;
        if (Text(job["userId"]) is { Length: > 0 } user && !string.IsNullOrWhiteSpace(discussion.OwnerId)
            && !string.Equals(user, discussion.OwnerId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (job["creationProvenance"] is not JsonObject provenance || provenance["context"] is not JsonArray context)
            return false;
        var namesDiscussion = false;
        foreach (var entry in context.OfType<JsonObject>())
        {
            var kind = Text(entry["kind"]);
            var value = Text(entry["id"]);
            if (kind == "discussion" && value == discussion.Id) namesDiscussion = true;
            // One session context: the session that made it. Another one's jobs are its own card's.
            if (kind == "session" && (string.IsNullOrWhiteSpace(discussion.SessionId) || value != discussion.SessionId))
                return false;
        }
        return namesDiscussion;
    }

    /// <summary>The fields a card reads, outputs moved to this discussion's own route.</summary>
    internal static JsonObject Project(JsonObject job, string discussionId)
    {
        var projected = new JsonObject();
        foreach (var field in ProjectedFields)
            if (job[field] is { } value) projected[field] = value.DeepClone();
        if (job["outputs"] is JsonArray outputs && IdOf(job) is { } jobId)
        {
            var rewritten = new JsonArray();
            for (var i = 0; i < outputs.Count; i++)
            {
                if (outputs[i] is not JsonObject output) continue;
                rewritten.Add(new JsonObject
                {
                    ["url"] = $"/api/apps/nova/discussions/{Uri.EscapeDataString(discussionId)}/jobs/{jobId:D}/output?index={i}",
                    ["contentType"] = output["contentType"]?.DeepClone(),
                    ["sizeBytes"] = output["sizeBytes"]?.DeepClone(),
                });
            }
            projected["outputs"] = rewritten;
        }
        return projected;
    }

    private sealed record DetailRead(JsonObject? Job, bool Gone, IResult? Failure);

    private static async Task<DetailRead> ReadDetailAsync(RedComputeClient redCompute, Guid jobId, CancellationToken ct)
    {
        RedComputeClient.ProxyResult result;
        try { result = await redCompute.GetJobAsync(jobId, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(null, false, Error(503, "redcompute_unavailable", ex.Message));
        }
        // RedCompute refusing the read and the job being gone are the same to a card: not there.
        if (result.StatusCode is 403 or 404) return new(null, true, null);
        if (result.StatusCode != 200) return new(null, false, Upstream(result, "read"));
        try
        {
            return JsonNode.Parse(result.Content) is JsonObject job
                ? new(job, false, null)
                : new(null, false, Error(502, "invalid_job", "RedCompute returned an invalid job"));
        }
        catch (JsonException)
        {
            return new(null, false, Error(502, "invalid_job", "RedCompute returned an invalid job"));
        }
    }

    private static async Task<(JsonObject? Job, IResult? Failure)> ReadOwnJobAsync(
        DiscussionRead discussion, Guid jobId, RedComputeClient redCompute, CancellationToken ct)
    {
        var read = await ReadDetailAsync(redCompute, jobId, ct);
        if (read.Failure is not null) return (null, read.Failure);
        if (read.Gone || read.Job is null || !Belongs(read.Job, discussion)) return (null, JobNotFound());
        return (read.Job, null);
    }

    private static List<JsonObject>? ReadItems(string content)
    {
        try
        {
            var root = JsonNode.Parse(content);
            var items = root is JsonObject page ? page["items"] : root;
            return items is JsonArray array ? array.OfType<JsonObject>().ToList() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsQuickSuccess(JsonObject job)
        => Text(job["status"]) == "Completed"
           && job["durationMs"] is JsonValue duration && duration.TryGetValue<double>(out var ms) && ms < QuickSuccessMs;

    private static Guid? IdOf(JsonObject job) => Guid.TryParse(Text(job["id"]), out var id) ? id : null;

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Bool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool TryParseInstant(string? raw, out DateTimeOffset instant)
    {
        instant = default;
        return !string.IsNullOrWhiteSpace(raw)
               && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);
    }

    private static DateTimeOffset Utc(DateTime value)
        => new(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static IResult Page(IEnumerable<JsonObject> items, DateTimeOffset since)
        => Results.Content(new JsonObject
        {
            ["items"] = new JsonArray(items.Cast<JsonNode>().ToArray()),
            ["since"] = since.ToUniversalTime().ToString("O"),
        }.ToJsonString(), "application/json");

    private static IResult Upstream(RedComputeClient.ProxyResult result, string what)
        => result.StatusCode is 400 or 403 or 503
            ? Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode)
            : Error(502, "redcompute_error", $"RedCompute answered the job {what} with HTTP {result.StatusCode}");

    private static IResult JobNotFound() => Error(404, "job_not_found", "No such job in this discussion");

    private static IResult Error(int status, string error, string message)
        => Results.Json(new { error, message }, statusCode: status);
}
