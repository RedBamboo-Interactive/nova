using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Leaf Community images reach the model as images: reviewed by the inbound Sentinel with the
/// message, then attached to the RedCompute turn. Native Discord is unchanged.
/// </summary>
public sealed partial class ExternalAgentConversationProviderTests
{
    private static readonly byte[] ShotPng = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3];

    [Fact]
    public async Task Commons_image_is_reviewed_with_the_message_and_attached_to_the_session_turn()
    {
        var run = await SendCommonsAsync(new RecordingSentinel(),
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
        var run = await SendCommonsAsync(new RecordingSentinel { EchoImages = false },
            [Attachment("1", "/api/assets/stable-shot.png")]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Single(Assert.Single(run.Sentinel.Requests).Images!);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task Commons_image_is_refused_when_the_sentinel_is_unavailable()
    {
        var run = await SendCommonsAsync(new RecordingSentinel { Verdict = UntrustedContentVerdict.Unavailable() },
            [Attachment("1", "/api/assets/stable-shot.png")]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Null(run.Message);
    }

    [Theory]
    [InlineData("/api/assets/missing.png")]
    [InlineData("/api/assets/../secret.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/shot.png")]
    [InlineData("/api/assets/owner-notes.txt")]
    public async Task A_commons_attachment_that_is_not_a_readable_image_refuses_the_message_unreviewed(string scopeToken)
    {
        var run = await SendCommonsAsync(new RecordingSentinel(), [Attachment("1", scopeToken)]);

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Empty(run.Sentinel.Requests);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task More_images_than_the_sentinel_reviews_refuse_the_message()
    {
        var run = await SendCommonsAsync(new RecordingSentinel(),
            Enumerable.Range(1, 5).Select(index => Attachment($"{index}", "/api/assets/stable-shot.png")).ToArray());

        Assert.Equal(ExternalConversationDispositions.RefusedSentinelUnavailable, run.Admission.Disposition);
        Assert.Null(run.Message);
    }

    [Fact]
    public async Task A_text_only_commons_message_sends_no_images()
    {
        var run = await SendCommonsAsync(new RecordingSentinel(), []);

        Assert.Equal("delivered", run.Admission.Disposition);
        Assert.Null(Assert.Single(run.Sentinel.Requests).Images);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.False(body.RootElement.TryGetProperty("images", out _));
    }

    [Fact]
    public async Task Native_discord_attachments_stay_metadata_and_are_not_sent_as_images()
    {
        var run = await SendCommonsAsync(new RecordingSentinel(),
            [Attachment("1", "/api/assets/stable-shot.png")], transport: "discord");

        Assert.Equal("delivered", run.Admission.Disposition);
        Assert.Null(Assert.Single(run.Sentinel.Requests).Images);
        using var body = JsonDocument.Parse(run.Message!);
        Assert.False(body.RootElement.TryGetProperty("images", out _));
    }

    [Theory]
    [InlineData("/api/assets/stable-abc.png", "stable-abc.png")]
    [InlineData("/api/assets/a/b.png", null)]
    [InlineData("/api/assets/", null)]
    [InlineData("api/assets/x.png", null)]
    [InlineData("/api/assets/x..png", null)]
    public void Commons_asset_references_are_exactly_local_asset_ids(string scopeToken, string? expected)
        => Assert.Equal(expected, ExternalAgentConversationProvider.CommonsAssetId(scopeToken));

    private sealed record CommonsRun(
        ExternalConversationAdmission Admission, RecordingSentinel Sentinel, string? Message);

    private static async Task<CommonsRun> SendCommonsAsync(
        RecordingSentinel sentinel, IReadOnlyList<ExternalConversationAttachment> attachments,
        string transport = "leaf-commons")
    {
        var agentId = Guid.NewGuid();
        var entities = new FakeEntityStore(
            Entity(agentId, "agent", "nova", "Nova"),
            Entity(Guid.NewGuid(), "plugin", NovaAppPlugin.PluginId, "Nova"));
        using var agents = new AgentDirectory(entities, new NoOpPluginEvents());
        var gateway = new ReusedSessionGateway();
        var verifier = new DiscordPromptInjectionVerifier(
            sentinel, agents, entities, NullLogger<DiscordPromptInjectionVerifier>.Instance);
        var provider = new ExternalAgentConversationProvider(
            null!, new RedComputeClient(gateway), agents, entities, verifier,
            NullLogger<ExternalAgentConversationProvider>.Instance, new CommonsAssets());
        var handle = new ExternalConversationHandle(
            "leaf-agent-session", "leaf-commons:binding", 1, "conversation-1", "session-1");
        var admission = await provider.SendAsync(handle, new ExternalConversationInput(
            "relay-message-1", new ExternalRequestor("discord:human-1", "Morgan"),
            "What is wrong here?", attachments, new JsonObject
            {
                ["transport"] = transport,
                ["application_id"] = "leaf-commons",
                ["agent_id"] = agentId.ToString(),
                ["binding_id"] = "leaf-commons:binding",
                ["owner_user_id"] = "owner-1",
                ["omitted_attachments"] = 2,
            }));
        var message = gateway.Requests.Where(request => request.Path == "/ai-session/sessions/session-1/message")
            .Select(request => request.Body).SingleOrDefault();
        return new(admission, sentinel, message);
    }

    private static ExternalConversationAttachment Attachment(string id, string scopeToken)
        => new(id, "shot.png", "image/png", ShotPng.Length, "sha", scopeToken);

    private sealed class CommonsAssets : IAssets
    {
        public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public string GetUrl(string assetId) => $"/api/assets/{assetId}";

        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
            => Task.FromResult<AssetFile?>(assetId switch
            {
                "stable-shot.png" => new AssetFile(ShotPng, "image/png"),
                "owner-notes.txt" => new AssetFile("private"u8.ToArray(), "application/octet-stream"),
                _ => null,
            });
    }
}
