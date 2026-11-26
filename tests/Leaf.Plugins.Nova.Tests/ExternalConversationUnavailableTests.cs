using System.Net;
using System.Text.Json;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 2026-11-26: leaf-discord logged "Discord transcript reconciliation failed ... RedCompute transcript is unavailable"
/// as an ERROR 30 times that day (each rebuild's drain and stop) and about 1,300 times on 24 Nov. A settled read folded
/// every failure into that one InvalidOperationException: RedCompute stopping or draining (5xx), no answer at all, a
/// missing session, even the caller's own cancellation. The caller could not tell a retry from a fault. RedCompute
/// being unavailable is an HttpRequestException now, a missing session stays an InvalidOperationException, and a
/// cancelled read is cancelled.
/// </summary>
public sealed class ExternalConversationUnavailableTests
{
    private static readonly ExternalConversationHandle Handle =
        new("leaf-agent-session", "binding-1", 1, "conversation-1", "session-1");

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task RedCompute_answering_5xx_for_the_session_is_unavailable_not_a_fault(HttpStatusCode status)
    {
        var provider = Provider(new Gateway(session: _ => new HttpResponseMessage(status)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadSettledAsync(Handle, null, 100));

        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task RedCompute_not_answering_is_unavailable_not_a_fault()
    {
        var provider = Provider(new Gateway(session: _ => throw new HttpRequestException("No connection could be made")));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadSettledAsync(Handle, null, 100));
    }

    [Fact]
    public async Task A_transcript_page_RedCompute_cannot_serve_is_unavailable_not_a_fault()
    {
        var provider = Provider(new Gateway(
            session: _ => Json("""{"session":{"status":"Idle"},"messages":[],"inputQueue":{"depth":0}}"""),
            page: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ReadSettledAsync(Handle, null, 100));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task A_session_RedCompute_does_not_have_is_still_a_fault()
    {
        var provider = Provider(new Gateway(session: _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadSettledAsync(Handle, null, 100));

        Assert.Contains("unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_read_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var provider = Provider(new Gateway(session: _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return null!;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadSettledAsync(Handle, null, 100, cts.Token));
    }

    private static ExternalAgentConversationProvider Provider(IComputeGateway gateway)
        => new(null!, new RedComputeClient(gateway), null!, null!, null!,
            NullLogger<ExternalAgentConversationProvider>.Instance);

    private static HttpResponseMessage Json(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private sealed class Gateway(
        Func<HttpRequestMessage, HttpResponseMessage> session,
        Func<HttpRequestMessage, HttpResponseMessage>? page = null) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            var respond = path.Contains("/transcript-page", StringComparison.Ordinal)
                ? page ?? (_ => Json(JsonSerializer.Serialize(new { })))
                : session;
            return Task.FromResult(respond(request));
        }
    }
}
