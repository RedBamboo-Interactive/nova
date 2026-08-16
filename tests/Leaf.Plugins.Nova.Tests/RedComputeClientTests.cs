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

    [Fact]
    public async Task SessionMutationsForwardStructuredProvenance()
    {
        var gateway = new RecordingComputeGateway();
        var client = new RedComputeClient(gateway);

        var provenance = Provenance();
        var sessionId = await client.CreateSessionAsync(new(), provenance);
        var sent = await client.SendMessageDetailedAsync(
            sessionId!, new { content = "queued" }, provenance);
        var queue = await client.ProxyInputQueueAsync(
            sessionId!, HttpMethod.Get);

        Assert.True(sent.Success);
        Assert.Equal(200, queue.StatusCode);
        Assert.Equal(3, gateway.Requests.Count);
        Assert.Equal(provenance, gateway.Requests[0].Provenance);
        Assert.Equal(provenance, gateway.Requests[1].Provenance);
        Assert.Null(gateway.Requests[2].Provenance);
    }

    private static ComputeProvenance Provenance() => new(
        ComputeProvenance.CurrentSchemaVersion,
        new ComputeOrigin("redleaf", new ComputeAppReference("app", "nova", null, "Nova"),
            new ComputeEntrypoint("http", "/api/apps/nova/test", "POST")),
        new ComputeActor("agent", "Nova", Id: "nova"),
        new ComputeBeneficiary("user", "user-1", "Laurent"),
        [], new ComputeTrace(), ComputeProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    private sealed class RecordingComputeGateway : IComputeGateway
    {
        public List<(string Path, ComputeProvenance? Provenance)> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.AbsolutePath
                : request.RequestUri?.OriginalString ?? "";
            Requests.Add((path, provenance));
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
