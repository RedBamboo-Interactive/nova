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
}
