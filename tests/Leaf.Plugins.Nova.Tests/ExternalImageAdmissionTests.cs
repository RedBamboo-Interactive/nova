using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Leaf Community and native Leaf Discord images reach the model as images: reviewed by the
/// inbound Sentinel with the message, then attached to the RedCompute turn.
/// </summary>
public sealed partial class ExternalAgentConversationProviderTests
{
    private static readonly byte[] ShotPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPkOiHHwMDAxMDAwMDAAAALVgD0I4hqmwAAAABJRU5ErkJggg==");
    private static readonly byte[] WidePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAEAEAAAABCAAAAACUiF+eAAAAGklEQVR4nO3BAQ0AAADCoPdPbQ8HFAAAAPBvEAIAAXV6XuIAAAAASUVORK5CYII=");
    private static readonly byte[] AnimatedWebp = Convert.FromBase64String(
        "UklGRjgBAABXRUJQVlA4WAoAAAACAAAAFwAAFwAAQU5JTQYAAAAAAAAAAABBTk1GrAAAAAAAAAAAABcAABcAAGQAAAJWUDgglAAAANAEAJ0BKhgAGAA+YSaPRaQiIZv0AEAGBLUWYAFSa4nKfxaTAATc/TaSNLLNb+gAAP79bxFYg42pNVBevwKFvfux//JinOJrrHIgnuw2T+akq4Sf7Y3zunG9919Qimrnjta4CcFoRiViCLelT4wev/MZ/9i3L//YazgpgU5OIAl9YYkyHNkq5sJnJTbv48b0Up2PAABBTk1GWAAAAAAAAAAAABcAABcAAGQAAABWUDggQAAAADQDAJ0BKhgAGAA+bTSWR4KAgAAA2JZQDJAoB+AALWkGO1AA/vCbQ//ILlhdcjX/8gP+QH/ID/+QH/6b2anzgAA=");
    private static readonly byte[] StillWebp = Convert.FromBase64String(
        "UklGRjAAAABXRUJQVlA4TCMAAAAvD8ADEJkyRPQ/NhHR/wCRtk0t3L/gwdOJGMBJngCWOkD9BwA=");
    private static readonly byte[] SmallJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCOiiivkD83P//Z");

    [Fact]
    public async Task Commons_image_is_reviewed_with_the_message_and_attached_to_the_session_turn()
    {
        var run = await SendAsync(new RecordingSentinel(),
            [Attachment("1", "/api/assets/stable-shot.png")]);

        Assert.Equal("delivered", run.Admission.Disposition);
        var review = Assert.Single(run.Sentinel.Requests);
        Assert.Equal(new UntrustedContentImage("stable-shot.png", "image/png"), Assert.Single(review.Images!));
        Assert.Contains("[attachment] shot.png (image/png)", review.Content);
        using var body = JsonDocument.Parse(run.Message!);
        var image = Assert.Single(body.RootElement.GetProperty("images").EnumerateArray().ToArray());
        Assert.Equal("image/png", image.GetProperty("mediaType").GetString());
        Assert.Equal(ShotPng, Convert.FromBase64String(image.GetProperty("base64").GetString()!));
        Assert.Equal("What is wrong here?", body.RootElement.GetProperty("displayContent").GetString());
        var envelope = ParseTaggedJson(body.RootElement.GetProperty("content").GetString()!, "external-input-json");
        Assert.Equal(2, envelope.GetProperty("omittedAttachments").GetInt32());
        Assert.Equal("shot.png", envelope.GetProperty("attachments")[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Commons_image_is_refused_when_the_review_did_not_see_it()
    {
        var run = await SendAsync(new RecordingSentinel { EchoImages = false },
            [Attachment("1", "/api/assets/stable-shot.png")]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Single(Assert.Single(run.Sentinel.Requests).Images!);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task Commons_image_is_refused_when_the_sentinel_is_unavailable()
    {
        var run = await SendAsync(new RecordingSentinel { Verdict = UntrustedContentVerdict.Unavailable() },
            [Attachment("1", "/api/assets/stable-shot.png")]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Null(run.Message);
    }

    [Theory]
    [InlineData("/api/assets/missing.png")]
    [InlineData("/api/assets/../secret.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/shot.png")]
    [InlineData("/api/assets/owner-notes.txt")]
    [InlineData("/api/assets/fake.png")]
    [InlineData("/api/assets/wide.png")]
    public async Task A_commons_attachment_that_is_not_a_readable_image_refuses_the_message_unreviewed(string scopeToken)
    {
        var run = await SendAsync(new RecordingSentinel(), [Attachment("1", scopeToken)]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Empty(run.Sentinel.Requests);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task More_images_than_the_sentinel_reviews_refuse_a_commons_message()
    {
        var run = await SendAsync(new RecordingSentinel(),
            Enumerable.Range(1, 5).Select(index => Attachment($"{index}", "/api/assets/stable-shot.png")).ToArray());

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Null(run.Message);
    }

    [Theory]
    [InlineData("leaf-commons")]
    [InlineData("discord")]
    public async Task A_text_only_message_sends_no_images(string transport)
    {
        var run = await SendAsync(new RecordingSentinel(), [], transport);

        Assert.Equal("delivered", run.Admission.Disposition);
        Assert.Null(Assert.Single(run.Sentinel.Requests).Images);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.False(body.RootElement.TryGetProperty("images", out _));
    }

    [Fact]
    public async Task Native_discord_images_are_reviewed_and_shown_while_other_files_stay_metadata()
    {
        var run = await SendAsync(new RecordingSentinel(),
        [
            Attachment("1", "/api/assets/stable-shot.png"),
            Attachment("2", "/api/assets/report.pdf", "report.pdf", "application/pdf"),
            Attachment("3", "/api/assets/photo.jpg", "photo.jpg", "image/jpeg"),
            Attachment("4", "/api/assets/fake.png"),
            Attachment("5", "/api/assets/wide.png"),
            Attachment("6", "/api/assets/animated.webp", "loop.webp", "image/webp"),
            Attachment("7", "/api/assets/missing.png"),
            Attachment("8", "/api/assets/still.webp", "still.webp", "image/webp"),
        ], transport: "discord");

        Assert.Equal("delivered", run.Admission.Disposition);
        var review = Assert.Single(run.Sentinel.Requests);
        Assert.Equal(UntrustedContentSources.Discord, review.Origin.Source);
        Assert.Equal(new[] { "stable-shot.png", "photo.jpg", "still.webp" }, review.Images!.Select(image => image.AssetId));
        // Every attachment, shown or not, is still named in the reviewed sample.
        Assert.Contains("[attachment] report.pdf (application/pdf)", review.Content);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.Equal(new[] { "image/png", "image/jpeg", "image/webp" }, body.RootElement.GetProperty("images")
            .EnumerateArray().Select(image => image.GetProperty("mediaType").GetString()));
        var envelope = ParseTaggedJson(body.RootElement.GetProperty("content").GetString()!, "discord-input-json");
        Assert.Equal(new[] { "1", "3", "8" }, envelope.GetProperty("shownImageIds").EnumerateArray()
            .Select(id => id.GetString()));
        Assert.Equal(8, envelope.GetProperty("attachments").GetArrayLength());
    }

    [Fact]
    public async Task Native_discord_shows_at_most_four_images()
    {
        var run = await SendAsync(new RecordingSentinel(),
            Enumerable.Range(1, 6).Select(index => Attachment($"{index}", "/api/assets/stable-shot.png")).ToArray(),
            transport: "discord");

        Assert.Equal(4, Assert.Single(run.Sentinel.Requests).Images!.Count);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.Equal(4, body.RootElement.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task Native_discord_image_message_is_refused_when_the_review_did_not_see_the_image()
    {
        var run = await SendAsync(new RecordingSentinel { EchoImages = false },
            [Attachment("1", "/api/assets/stable-shot.png")], transport: "discord");

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task Native_discord_message_with_only_non_image_files_is_reviewed_as_before()
    {
        var run = await SendAsync(new RecordingSentinel { EchoImages = false },
            [Attachment("1", "/api/assets/report.pdf", "report.pdf", "application/pdf")], transport: "discord");

        Assert.Equal("delivered", run.Admission.Disposition);
        Assert.Null(Assert.Single(run.Sentinel.Requests).Images);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.False(body.RootElement.TryGetProperty("images", out _));
    }

    [Theory]
    [InlineData("leaf-commons", "vision_unavailable")]
    [InlineData("discord", "image_attachments_not_supported")]
    public async Task A_session_that_refuses_images_still_gets_the_reviewed_turn_without_them(string transport, string error)
    {
        var run = await SendAsync(new RecordingSentinel(), [Attachment("1", "/api/assets/stable-shot.png")],
            transport, refuseImages: error);

        Assert.Equal("delivered", run.Admission.Disposition);
        Assert.Equal(2, run.Messages.Count);
        using var first = JsonDocument.Parse(run.Messages[0]);
        Assert.True(first.RootElement.TryGetProperty("images", out _));
        using var retry = JsonDocument.Parse(run.Messages[1]);
        Assert.False(retry.RootElement.TryGetProperty("images", out _));
        if (transport == "discord")
            Assert.Empty(ParseTaggedJson(retry.RootElement.GetProperty("content").GetString()!, "discord-input-json")
                .GetProperty("shownImageIds").EnumerateArray());
    }

    [Theory]
    [InlineData("/api/assets/stable-abc.png", "stable-abc.png")]
    [InlineData("/api/assets/a/b.png", null)]
    [InlineData("/api/assets/", null)]
    [InlineData("api/assets/x.png", null)]
    [InlineData("/api/assets/x..png", null)]
    public void Asset_references_are_exactly_local_asset_ids(string scopeToken, string? expected)
        => Assert.Equal(expected, ExternalAgentConversationProvider.AssetIdFromReference(scopeToken));

    [Fact]
    public void Showable_images_are_judged_from_their_bytes()
    {
        Assert.Equal("image/png", ExternalConversationImages.Displayable(ShotPng));
        Assert.Equal("image/jpeg", ExternalConversationImages.Displayable(SmallJpeg));
        Assert.Equal("image/webp", ExternalConversationImages.Displayable(StillWebp));
        Assert.Null(ExternalConversationImages.Displayable(AnimatedWebp));
        Assert.Null(ExternalConversationImages.Displayable(WidePng));
        Assert.Null(ExternalConversationImages.Displayable("not an image"u8.ToArray()));
        var huge = new byte[ExternalConversationImages.MaxImageBytes + 1];
        ShotPng.CopyTo(huge, 0);
        Assert.Null(ExternalConversationImages.Displayable(huge));
    }

    private sealed record ImageRun(
        ExternalConversationAdmission Admission, RecordingSentinel Sentinel, IReadOnlyList<string> Messages)
    {
        public string? Message => Messages.LastOrDefault();
    }

    private static async Task<ImageRun> SendAsync(
        RecordingSentinel sentinel, IReadOnlyList<ExternalConversationAttachment> attachments,
        string transport = "leaf-commons", string? refuseImages = null)
    {
        var agentId = Guid.NewGuid();
        var entities = new FakeEntityStore(
            Entity(agentId, "agent", "nova", "Nova"),
            Entity(Guid.NewGuid(), "plugin", NovaAppPlugin.PluginId, "Nova"));
        using var agents = new AgentDirectory(entities, new NoOpPluginEvents());
        var gateway = new ImageGateway(refuseImages);
        var verifier = new DiscordPromptInjectionVerifier(
            sentinel, agents, entities, NullLogger<DiscordPromptInjectionVerifier>.Instance);
        var provider = new ExternalAgentConversationProvider(
            null!, new RedComputeClient(gateway), agents, entities, verifier,
            NullLogger<ExternalAgentConversationProvider>.Instance, new ImageAssets());
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "binding-1", 1, "conversation-1", "session-1");
        var admission = await provider.SendAsync(handle, new ExternalConversationInput(
            "message-1", new ExternalRequestor("discord:human-1", "Morgan"),
            "What is wrong here?", attachments, new JsonObject
            {
                ["transport"] = transport,
                ["application_id"] = "leaf-commons",
                ["agent_id"] = agentId.ToString(),
                ["binding_id"] = "binding-1",
                ["owner_user_id"] = "owner-1",
                ["omitted_attachments"] = 2,
            }));
        return new(admission, sentinel, gateway.Messages);
    }

    private static ExternalConversationAttachment Attachment(
        string id, string scopeToken, string name = "shot.png", string mediaType = "image/png")
        => new(id, name, mediaType, 100, "sha", scopeToken);

    /// <summary>A RedCompute session that may refuse the first turn that carries images.</summary>
    private sealed class ImageGateway(string? refuseImages) : IComputeGateway
    {
        public List<string> Messages { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """{"session":{"status":"Idle","providerSessionId":"thread-1"}}""");
            if (!path.EndsWith("/message", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{}");
            Messages.Add(body);
            return refuseImages is not null && body.Contains("\"images\"", StringComparison.Ordinal)
                ? Json(HttpStatusCode.UnprocessableEntity, $$"""{"error":"{{refuseImages}}","message":"no"}""")
                : Json(HttpStatusCode.OK, """{"disposition":"delivered","queueItemId":"queue-1"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content)
            => new(status) { Content = new StringContent(content) };
    }

    private sealed class ImageAssets : IAssets
    {
        public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public string GetUrl(string assetId) => $"/api/assets/{assetId}";

        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult<AssetFile?>(assetId switch
            {
                "stable-shot.png" => new AssetFile(ShotPng, "image/png"),
                "photo.jpg" => new AssetFile(SmallJpeg, "image/jpeg"),
                "still.webp" => new AssetFile(StillWebp, "image/webp"),
                "animated.webp" => new AssetFile(AnimatedWebp, "image/webp"),
                "wide.png" => new AssetFile(WidePng, "image/png"),
                "fake.png" => new AssetFile("<html>not a png</html>"u8.ToArray(), "image/png"),
                "report.pdf" => new AssetFile("%PDF-1.7"u8.ToArray(), "application/octet-stream"),
                "owner-notes.txt" => new AssetFile("private"u8.ToArray(), "application/octet-stream"),
                _ => null,
            });
    }
}
