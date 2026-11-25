using System.Net;
using System.Text;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// A refused session start says why: RedCompute's own code and message, or RedLeaf's identity refusal,
// never "RedCompute could not be reached" for an answer that did arrive.
public sealed class SessionCreateRefusalTests
{
    [Fact]
    public async Task A_503_that_is_not_maintenance_is_read_once_and_reported_with_its_code()
    {
        // Production response content can be read only once; StringContent would hide a second read.
        var client = new RedComputeClient(new OneShotGateway(HttpStatusCode.ServiceUnavailable,
            """{"error":"scratch_unavailable","message":"The placement's scratch folder is not available on this node"}"""));

        var (sessionId, refusal) = await client.CreateSessionWithReasonAsync(new(), null!);

        Assert.Null(sessionId);
        Assert.Equal(new SessionCreateRefusal(503, "scratch_unavailable",
            "The placement's scratch folder is not available on this node"), refusal);
        Assert.Equal("RedCompute refused to start a session (scratch_unavailable): The placement's scratch folder is not available on this node",
            refusal!.Describe());
    }

    [Fact]
    public async Task A_maintenance_503_read_once_still_throws_maintenance()
    {
        var client = new RedComputeClient(new OneShotGateway(HttpStatusCode.ServiceUnavailable,
            """{"error":"maintenance_draining","message":"raw upstream message"}"""));

        await Assert.ThrowsAsync<ComputeMaintenanceException>(() => client.CreateSessionWithReasonAsync(new(), null!));
    }

    [Fact]
    public async Task RedLeafs_identity_refusal_is_a_refusal_not_an_unreachable_RedCompute()
    {
        var client = new RedComputeClient(new ThrowingGateway(
            new ExecutionIdentityValidationException("Compute provenance beneficiary does not match the authenticated caller")));

        var (sessionId, refusal) = await client.CreateSessionWithReasonAsync(new(), null!);

        Assert.Null(sessionId);
        Assert.Equal(403, refusal!.Status);
        Assert.Equal(SessionCreateRefusal.IdentityRefused, refusal.Code);
        Assert.StartsWith("This session cannot start for whoever is signed in here: Compute provenance beneficiary",
            refusal.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(502, "Bad gateway from a proxy", "http_502", "Bad gateway from a proxy")]
    [InlineData(500, "", "http_500", "RedCompute answered HTTP 500")]
    [InlineData(409, """{"code":"node_offline","message":"The node is offline."}""", "node_offline", "The node is offline.")]
    [InlineData(422, """{"error":"validation_failed"}""", "validation_failed", """{"error":"validation_failed"}""")]
    public void Error_bodies_of_any_shape_give_a_code_and_a_message(int status, string body, string code, string message)
    {
        var refusal = SessionCreateRefusal.FromResponse(status, body);
        Assert.Equal(code, refusal.Code);
        Assert.Equal(message, refusal.Message);
    }

    [Fact]
    public void A_long_body_is_cut_to_500_characters()
        => Assert.Equal(500, SessionCreateRefusal.FromResponse(502, new string('x', 2000)).Message.Length);

    private sealed class OneShotGateway(HttpStatusCode status, string body) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StreamContent(new OneShotStream(Encoding.UTF8.GetBytes(body))),
            });
    }

    private sealed class ThrowingGateway(Exception exception) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    /// <summary>Forward-only, like a network response body.</summary>
    private sealed class OneShotStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ExecutionIdentityValidationException(string message) : Exception(message);
}
