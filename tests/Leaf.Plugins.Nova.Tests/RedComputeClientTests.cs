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
    public async Task TranscriptPageCarriesCanonicalIdentityStorageBoundaryAndAttachments()
    {
        var gateway = new StaticComputeGateway(
            """{"session":{"id":"session-1","status":"Idle","title":"Refined"},"messages":[{"id":42,"sessionId":"session-1","role":"user","eventType":"text","content":"hello","messageId":"provider-message","messageUid":"stable-uid","epoch":"epoch-1","sequence":81,"timestamp":"2026-09-12T12:00:00Z","recordCreatedAt":"2026-09-12T12:00:01Z","attachmentsJson":"[]"}],"page":{"epoch":"epoch-1","direction":"newest","oldestCursor":"older","newestCursor":"newer","hasEarlier":true,"hasLater":false,"fromSequence":81,"throughSequence":81,"boundaryComplete":true}}""");
        var client = new RedComputeClient(gateway);

        var result = await client.GetTranscriptPageAsync("session-1", 500);

        Assert.True(result.Success);
        var page = Assert.IsType<SessionTranscriptPage>(result.Value);
        var message = Assert.Single(page.Messages);
        Assert.Equal(42, message.Id);
        Assert.Equal("session-1", message.SessionId);
        Assert.Equal("provider-message", message.MessageId);
        Assert.Equal("epoch-1", message.Epoch);
        Assert.Equal(81, message.Sequence);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T12:00:01Z"), message.RecordCreatedAt);
        Assert.Equal("[]", message.AttachmentsJson);
        Assert.True(page.Page.HasEarlier);
        Assert.Equal("newer", page.Page.NewestCursor);
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
    public async Task StatelessGenerationCarriesFastTierConfidentialityAndProvenance()
    {
        var gateway = new GenerateComputeGateway();
        var client = new RedComputeClient(gateway);
        var provenance = Provenance();

        var result = await client.GenerateAsync(new
        {
            mode = "oneshot",
            qualityTier = "fast",
            confidential = true,
        }, "Update title", provenance);

        Assert.True(result.Success);
        Assert.Equal("A concise title", result.Text);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), result.JobId);
        Assert.Equal("/ai-session/generate", gateway.Path);
        Assert.Equal("Update title", gateway.JobName);
        Assert.Equal(provenance, gateway.Provenance);
        Assert.Contains("\"qualityTier\":\"fast\"", gateway.Body);
        Assert.Contains("\"confidential\":true", gateway.Body);
    }

    [Fact]
    public async Task InputAttachmentDownloadUsesAuthorizedGatewayWithoutLeakingItsUrl()
    {
        var gateway = new BinaryComputeGateway();
        var client = new RedComputeClient(gateway);

        var attachment = await client.GetInputAttachmentAsync("att image/1");

        Assert.NotNull(attachment);
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Bytes);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal("/ai-session/input-attachments/att%20image%2F1", gateway.Path);
        Assert.Null(gateway.Provenance);
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

    private sealed class GenerateComputeGateway : IComputeGateway
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? JobName { get; private set; }
        public ComputeProvenance? Provenance { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Path = request.RequestUri?.OriginalString;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            JobName = request.Headers.TryGetValues("X-Job-Name", out var values)
                ? values.SingleOrDefault() : null;
            Provenance = provenance;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"success\":true,\"text\":\"A concise title\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-Job-Id", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            return response;
        }
    }

    private sealed class BinaryComputeGateway : IComputeGateway
    {
        public string? Path { get; private set; }
        public ComputeProvenance? Provenance { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Path = request.RequestUri?.OriginalString;
            Provenance = provenance;
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
