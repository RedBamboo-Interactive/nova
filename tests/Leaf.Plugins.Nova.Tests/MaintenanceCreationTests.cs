using System.Net;
using System.Text;
using Leaf.Plugins.Nova;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class MaintenanceCreationTests
{
    [Fact]
    public async Task Specific_503_refusal_survives_creation_with_local_draft_guidance()
    {
        var client = new RedComputeClient(new Gateway(HttpStatusCode.ServiceUnavailable,
            "{\"error\":\"maintenance_draining\",\"message\":\"raw upstream message\"}"));
        var failure = await Assert.ThrowsAsync<ComputeMaintenanceException>(() => client.CreateSessionAsync(new(), null!));
        Assert.Contains("Not sent", failure.Message);
        Assert.Contains("this device", failure.Message);
        Assert.DoesNotContain("raw upstream", failure.Message);
    }

    [Theory]
    [InlineData(503, "{\"error\":\"provider_unavailable\"}")]
    [InlineData(503, "not json")]
    [InlineData(403, "{\"error\":\"maintenance_draining\"}")]
    public async Task Other_failures_are_not_misclassified_as_maintenance(int status, string body)
    {
        var client = new RedComputeClient(new Gateway((HttpStatusCode)status, body));
        Assert.Null(await client.CreateSessionAsync(new(), null!));
    }

    [Fact]
    public async Task Success_keeps_session_contract_and_status_read_uses_gateway()
    {
        var gateway = new Gateway(HttpStatusCode.OK, "{\"id\":\"session-a\"}");
        var client = new RedComputeClient(gateway);
        Assert.Equal("session-a", await client.CreateSessionAsync(new(), null!));
        var status = await client.GetMaintenanceStatusAsync();
        Assert.Equal("/maintenance/status", gateway.Path);
        Assert.Equal(200, status.StatusCode);
    }

    private sealed class Gateway(HttpStatusCode status, string body) : IComputeGateway
    {
        public string? Path { get; private set; }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            Path = request.RequestUri?.OriginalString;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
