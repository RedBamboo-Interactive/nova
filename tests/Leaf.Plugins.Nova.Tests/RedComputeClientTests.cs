using System.Text.Json;
using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class RedComputeClientTests
{
    [Fact]
    public void SessionMessageContractCarriesProviderNeutralUid()
    {
        using var document = JsonDocument.Parse(
            """{"role":"user","eventType":"text","content":"hello","timestamp":"2026-08-07T09:48:45Z","messageUid":"stable-uid"}""");
        var message = RedComputeClient.ParseSessionMessage(document.RootElement);

        Assert.Equal("stable-uid", message.MessageUid);
        Assert.Equal("hello", message.Content);
    }
}
