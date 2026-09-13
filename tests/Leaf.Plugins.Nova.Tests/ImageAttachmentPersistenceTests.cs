using System.Text;
using System.Text.Json;
using Leaf.Plugins.Nova;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ImageAttachmentPersistenceTests
{
    private const string PartsJson =
        """[{"type":"image","assetId":"asset-1","url":"/api/assets/asset-1.webp","mediaType":"image/webp"}]""";
    private const string FilePartsJson =
        """[{"type":"attachment","id":"att_123","kind":"file","name":"proposal.pdf","mediaType":"application/pdf","size":284193,"sha256":"abc","downloadUrl":"/ai-session/input-attachments/att_123"}]""";

    [Fact]
    public void ReloadedUserMessageKeepsTextBeforeItsImage()
    {
        var parts = DiscussionEndpoints.MapUserMessageParts(PartsJson, "Describe this");

        var json = JsonSerializer.SerializeToElement(parts);
        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("text", json[0].GetProperty("type").GetString());
        Assert.Equal("Describe this", json[0].GetProperty("content").GetString());
        Assert.Equal("image", json[1].GetProperty("type").GetString());
        Assert.Equal("/api/assets/asset-1.webp", json[1].GetProperty("url").GetString());
    }

    [Fact]
    public void ReloadedUserMessageCanBeImageOnly()
    {
        var parts = DiscussionEndpoints.MapUserMessageParts(PartsJson, "");

        var json = JsonSerializer.SerializeToElement(parts);
        Assert.Single(json.EnumerateArray());
        Assert.Equal("image", json[0].GetProperty("type").GetString());
    }

    [Fact]
    public void ExportIncludesAStableImageReference()
    {
        var export = new StringBuilder();

        ConversationExporter.AppendImageParts(export, PartsJson);

        Assert.Equal("![attached image](/api/assets/asset-1.webp)" + Environment.NewLine, export.ToString());
    }

    [Fact]
    public void ReloadedUserMessageKeepsProviderAttachmentMetadata()
    {
        var parts = DiscussionEndpoints.MapUserMessageParts(FilePartsJson, "Review this");

        var json = JsonSerializer.SerializeToElement(parts);
        Assert.Equal("Review this", json[0].GetProperty("content").GetString());
        var attachment = json[1].GetProperty("attachments")[0];
        Assert.Equal("att_123", attachment.GetProperty("id").GetString());
        Assert.Equal("proposal.pdf", attachment.GetProperty("name").GetString());
        Assert.Equal(284193, attachment.GetProperty("size").GetInt64());
    }

    [Fact]
    public void ExportIncludesDownloadableFileReference()
    {
        var export = new StringBuilder();

        ConversationExporter.AppendImageParts(export, FilePartsJson);

        Assert.Equal("[proposal.pdf](/ai-session/input-attachments/att_123?download=true)" + Environment.NewLine, export.ToString());
    }

    [Fact]
    public void ShareExtractionFindsPersistedRedLeafImage()
    {
        var image = Assert.Single(NovaShareEnricher.ExtractShareImageSources(PartsJson));

        Assert.Equal("asset", image.Kind);
        Assert.Equal("asset-1", image.Id);
        Assert.Equal("image/webp", image.MediaType);
    }

    [Fact]
    public void ShareExtractionFindsClaimedRedComputeImageAndIgnoresFiles()
    {
        const string json =
            """{"attachments":[{"id":"att_image","kind":"image","name":"terrain.png","mediaType":"image/png"},{"id":"att_file","kind":"file","name":"notes.pdf","mediaType":"application/pdf"}]}""";

        var image = Assert.Single(NovaShareEnricher.ExtractShareImageSources(json));

        Assert.Equal("attachment", image.Kind);
        Assert.Equal("att_image", image.Id);
        Assert.Equal("terrain.png", image.AltText);
    }

    [Fact]
    public void ShareExtractionKeepsLegacyInlineImageBytes()
    {
        const string json = """{"images":[{"mediaType":"image/jpeg","base64":"aW1hZ2U="}]}""";

        var image = Assert.Single(NovaShareEnricher.ExtractShareImageSources(json));

        Assert.Equal("inline", image.Kind);
        Assert.Equal("aW1hZ2U=", image.Base64);
        Assert.Equal("image/jpeg", image.MediaType);
    }

    [Fact]
    public async Task ShareResolutionCopiesAuthorizedBytesAndPrefersTranscriptAttachments()
    {
        const string transcript =
            """{"attachments":[{"id":"att_image","kind":"image","name":"terrain.png","mediaType":"image/png"}]}""";
        var gateway = new AttachmentGateway();
        var enricher = new NovaShareEnricher(
            null!, null!, new RedComputeClient(gateway), null!, new AssetStub());

        var images = await enricher.ResolveShareImagesAsync(transcript, PartsJson, CancellationToken.None);

        var image = Assert.Single(images);
        Assert.Equal("AQID", image.Base64);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("terrain.png", image.AltText);
        Assert.Equal("/ai-session/input-attachments/att_image", gateway.Path);
    }

    [Fact]
    public async Task ShareResolutionCopiesPersistedRedLeafAssetWhenTranscriptHasNoImage()
    {
        var enricher = new NovaShareEnricher(
            null!, null!, new RedComputeClient(new AttachmentGateway()), null!, new AssetStub());

        var images = await enricher.ResolveShareImagesAsync(null, PartsJson, CancellationToken.None);

        var image = Assert.Single(images);
        Assert.Equal("BAUG", image.Base64);
        Assert.Equal("image/webp", image.MediaType);
    }

    private sealed class AttachmentGateway : IComputeGateway
    {
        public string? Path { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Path = request.RequestUri?.OriginalString;
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class AssetStub : IAssets
    {
        public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public string GetUrl(string assetId) => $"/api/assets/{assetId}";

        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default) =>
            Task.FromResult<AssetFile?>(assetId == "asset-1"
                ? new AssetFile([4, 5, 6], "image/webp")
                : null);
    }
}
