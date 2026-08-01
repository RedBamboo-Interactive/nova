using System.Text;
using System.Text.Json;
using Leaf.Plugins.Nova;
using Leaf.Plugins.Nova.Endpoints;
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
}
