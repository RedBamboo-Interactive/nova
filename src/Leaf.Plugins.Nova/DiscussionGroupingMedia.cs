using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed record DiscussionGroupingImageChoice(string Id, string DiscussionId, string Target);

internal sealed record DiscussionGroupingImage(DiscussionGroupingImageChoice Choice, string? Caption, AssetFile File);

internal sealed class DiscussionGroupingMedia(RedComputeClient compute, IAssets? assets)
{
    // RedCompute's stateless generate accepts at most four images.
    private const int ImageLimit = 4;
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly Regex AssetPath = new(
        @"^/api/assets/(?<file>[a-zA-Z0-9_-]+\.(?:png|jpe?g|webp))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record Candidate(DiscussionRead Discussion, string Target, string? Caption, bool IsImageOutput);

    public async Task<DiscussionGroupingImage[]> ReadAsync(DiscussionRead[] discussions, CancellationToken ct)
    {
        var pools = new List<Candidate[]>();
        foreach (var discussion in discussions.Where(d => d.SessionId is not null)
                     .OrderByDescending(d => d.LastActivity).Take(12))
        {
            try
            {
                var reply = await compute.GetSessionArtifactsAsync(discussion.SessionId!, kinds: "image", limit: "8", ct: ct);
                if (reply.StatusCode != 200) continue;
                var rewritten = DiscussionEndpoints.RewriteArtifactTargets(reply.Content, discussion.Id, discussion.SessionId!);
                if (rewritten is null) continue;
                var items = JsonNode.Parse(rewritten)?["items"]?.AsArray();
                if (items is null) continue;
                var outputPrefix = OutputPrefix(discussion.Id);
                var candidates = items.OfType<JsonObject>()
                    .Where(item => Text(item, "kind") == "image" && Text(item, "role") == "assistant")
                    .Select(item => new Candidate(discussion, Text(item, "target") ?? "", Text(item, "title"),
                        Text(item, "origin") == "assistant-image"))
                    .Where(item => AssetPath.IsMatch(item.Target) || RecordId(item.Target, outputPrefix) > 0)
                    .DistinctBy(item => item.Target).ToArray();
                var authoredImages = candidates.Where(item => item.IsImageOutput).Take(ImageLimit).ToArray();
                if (candidates.Length > 0)
                    pools.Add(authoredImages.Length > 0 ? authoredImages : candidates.Take(ImageLimit).ToArray());
            }
            catch (Exception ex) when ((ex is HttpRequestException or JsonException or InvalidOperationException
                or OperationCanceledException) && !ct.IsCancellationRequested)
            { /* Missing or unsupported artifact catalogs can still use icons. */ }
        }

        var result = new List<DiscussionGroupingImage>();
        for (var index = 0; index < ImageLimit && result.Count < ImageLimit; index++)
        {
            foreach (var pool in pools)
            {
                if (index >= pool.Length) continue;
                var candidate = pool[index];
                AssetFile? file;
                try { file = await ReadFileAsync(candidate, ct); }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException)
                    && !ct.IsCancellationRequested) { continue; }
                if (file is null || file.Bytes.Length is 0 or > MaxBytes
                    || file.ContentType is not ("image/png" or "image/jpeg" or "image/webp")
                    || ExternalConversationImages.Displayable(file.Bytes) != file.ContentType) continue;
                var caption = candidate.Caption;
                if (caption?.Length > 180) caption = caption[..180];
                result.Add(new(new($"image-{result.Count + 1}", candidate.Discussion.Id, candidate.Target), caption, file));
                if (result.Count == ImageLimit) break;
            }
        }
        return result.ToArray();
    }

    private async Task<AssetFile?> ReadFileAsync(Candidate candidate, CancellationToken ct)
    {
        var asset = AssetPath.Match(candidate.Target);
        if (asset.Success)
        {
            if (assets is null) return null;
            var id = asset.Groups["file"].Value;
            var record = await assets.GetRecordAsync(id, ct);
            if (record is { Confidential: true } or { SizeBytes: > MaxBytes }) return null;
            return await assets.ReadAsync(id, ct);
        }

        var idInSession = RecordId(candidate.Target, OutputPrefix(candidate.Discussion.Id));
        using var response = await compute.OpenTranscriptOutputAsync(candidate.Discussion.SessionId!, idInSession, ct: ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes
            || response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg" or "image/webp")) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes + 1];
        var size = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
        return size <= MaxBytes ? new(buffer[..size], response.Content.Headers.ContentType.MediaType) : null;
    }

    private static string OutputPrefix(string discussionId)
        => $"/api/apps/nova/discussions/{Uri.EscapeDataString(discussionId)}/messages/";

    private static long RecordId(string target, string prefix)
        => target.StartsWith(prefix, StringComparison.Ordinal) && target.EndsWith("/output", StringComparison.Ordinal)
            && long.TryParse(target.AsSpan(prefix.Length, target.Length - prefix.Length - "/output".Length), out var id)
            ? id : 0;

    private static string? Text(JsonObject item, string key)
        => item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
