using System.Text.Json;
using Leaf.Plugins.Nova;
using Leaf.Sdk.Services;
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

    [Theory]
    [InlineData(null, "local-user")]
    [InlineData("", "local-user")]
    [InlineData("user-42", "user-42")]
    public void OwnerIdentityIsCanonical(string? ownerUserId, string expected)
    {
        Assert.Equal(expected, RedComputeClient.CanonicalOwnerUserId(ownerUserId));
    }

    [Fact]
    public async Task SessionMessageAndQueueUseTheSameForwardedOwner()
    {
        var gateway = new RecordingComputeGateway();
        var client = new RedComputeClient(gateway);

        var sessionId = await client.CreateSessionAsync(new(), userId: null);
        var sent = await client.SendMessageDetailedAsync(
            sessionId!, new { content = "queued" }, ownerUserId: null);
        var queue = await client.ProxyInputQueueAsync(
            sessionId!, ownerUserId: "local-user", HttpMethod.Get);

        Assert.True(sent.Success);
        Assert.Equal(200, queue.StatusCode);
        Assert.Equal(3, gateway.Requests.Count);
        Assert.All(gateway.Requests, request =>
            Assert.Equal("local-user", request.OwnerUserId));
    }

    private sealed class RecordingComputeGateway : IComputeGateway
    {
        public List<(string Path, string? OwnerUserId)> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var owner = request.Headers.TryGetValues("X-User-Id", out var values)
                ? values.SingleOrDefault()
                : null;
            var path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.AbsolutePath
                : request.RequestUri?.OriginalString ?? "";
            Requests.Add((path, owner));
            var content = path.EndsWith("/sessions", StringComparison.Ordinal)
                ? "{\"id\":\"session-1\"}"
                : path.EndsWith("/message", StringComparison.Ordinal)
                    ? "{\"sent\":true}"
                    : "{\"items\":[],\"queue\":{\"depth\":0,\"state\":\"empty\"}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
