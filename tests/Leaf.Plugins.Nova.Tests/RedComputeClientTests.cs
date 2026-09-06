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
    public void SessionMessageContractCarriesCodexMessagePhase()
    {
        using var document = JsonDocument.Parse(
            """{"role":"assistant","eventType":"text","content":"done","phase":"final_answer","timestamp":"2026-08-21T08:00:00Z","messageUid":"turn-1"}""");

        var message = RedComputeClient.ParseSessionMessage(document.RootElement);

        Assert.Equal("final_answer", message.Phase);
    }

    [Fact]
    public void SessionMessageContractCarriesToolTranscriptFields()
    {
        using var document = JsonDocument.Parse(
            """{"role":"assistant","eventType":"tool_result","toolName":"Bash","toolInput":{"command":"pwd"},"payloadRef":{"recordId":42,"kind":"tool-output","length":12,"contentType":"text/plain","encoding":"utf-8","sha256":"abc","available":true},"timestamp":"2026-08-16T15:25:28Z","messageUid":"turn-1"}""");

        var message = RedComputeClient.ParseSessionMessage(document.RootElement);

        Assert.Equal("Bash", message.ToolName);
        Assert.Equal("{\"command\":\"pwd\"}", message.ToolInput);
        Assert.Equal(42, message.PayloadRef?.GetProperty("recordId").GetInt32());
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

    [Fact]
    public async Task SessionProbeCarriesRecoveryStateAndProviderThread()
    {
        var gateway = new StaticComputeGateway(
            """{"session":{"status":"Stopped","stopReason":"maintenance_restart","providerSessionId":"thread-1"}}""");
        var client = new RedComputeClient(gateway);

        var probe = await client.ProbeSessionAsync("session-1");

        Assert.True(probe.Reachable);
        Assert.Equal("Stopped", probe.Status);
        Assert.Equal("maintenance_restart", probe.StopReason);
        Assert.Equal("thread-1", probe.ProviderSessionId);
    }

    [Theory]
    [InlineData("Stopped", "maintenance_restart", true)]
    [InlineData("Stopped", "orphaned_on_restart", true)]
    [InlineData("Stopped", "process_exited", true)]
    [InlineData("Stopped", null, true)]
    [InlineData("Error", "provider_fault", true)]
    [InlineData("Stopped", "user_stopped", false)]
    [InlineData("Error", "usage_limit", false)]
    [InlineData("Idle", null, false)]
    public void PresenceRecoveryRespectsInfrastructureAndExplicitStopBoundaries(
        string status, string? stopReason, bool expected)
    {
        var probe = new RedComputeClient.SessionProbe(
            true, status, stopReason, "provider-thread");

        Assert.Equal(expected, HeartbeatService.ShouldAutoResumePresence(probe));
    }

    [Fact]
    public void PresenceRecoveryRequiresReachableResumableProviderState()
    {
        Assert.False(HeartbeatService.ShouldAutoResumePresence(
            new RedComputeClient.SessionProbe(false, "Stopped", "maintenance_restart", "thread")));
        Assert.False(HeartbeatService.ShouldAutoResumePresence(
            new RedComputeClient.SessionProbe(true, "Stopped", "maintenance_restart", null)));
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

    private sealed class StaticComputeGateway(string payload) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
