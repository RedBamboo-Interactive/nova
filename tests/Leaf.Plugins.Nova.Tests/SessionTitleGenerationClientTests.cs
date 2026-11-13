using System.Net;
using System.Text;
using System.Text.Json;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Nova's "Update title" asks RedCompute's one title generator for a title (apply=false, so
// RedCompute stores nothing) under Nova's provenance; the discussion store's policy keeps it.
public sealed class SessionTitleGenerationClientTests
{
    private static ComputeProvenance Provenance() => new(
        ComputeProvenance.CurrentSchemaVersion,
        new ComputeOrigin("redleaf",
            new ComputeAppReference("plugin", "nova", null, "Nova"),
            new ComputeEntrypoint("http", "/api/apps/nova/discussions/d1/title/generate", "POST")),
        new ComputeActor("agent", "Nova", Id: "nova"),
        new ComputeBeneficiary("user", "user-1", "Laurent"),
        [new ComputeContextReference("discussion", "d1"), new ComputeContextReference("session", "s/1")],
        new ComputeTrace(), ComputeProvenanceAssurance.Verified,
        DateTimeOffset.UtcNow);

    private static (RedComputeClient Client, List<(HttpRequestMessage Request, ComputeProvenance? Provenance)> Sent) Client(
        HttpStatusCode status, object body)
    {
        var sent = new List<(HttpRequestMessage, ComputeProvenance?)>();
        var gateway = CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, args) =>
        {
            sent.Add(((HttpRequestMessage)args[0]!, (ComputeProvenance?)args[1]));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        });
        return (new RedComputeClient(gateway), sent);
    }

    [Fact]
    public async Task Asks_RedCompute_for_a_title_it_does_not_store_under_Novas_provenance()
    {
        var (client, sent) = Client(HttpStatusCode.OK, new { title = "Paging earlier history", jobId = "job-1", qualityTier = "fast", session = new { id = "s/1" } });
        var provenance = Provenance();

        var result = await client.GenerateSessionTitleAsync("s/1", "Update Nova discussion title", provenance);

        Assert.True(result.Success);
        Assert.Equal("Paging earlier history", result.Title);
        Assert.Equal("job-1", result.JobId);
        var (request, sentProvenance) = Assert.Single(sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/ai-session/sessions/s%2F1/title/generate?apply=false", request.RequestUri!.OriginalString);
        Assert.Equal("Update Nova discussion title", request.Headers.GetValues("X-Job-Name").Single());
        Assert.Same(provenance, sentProvenance);
    }

    [Fact]
    public async Task An_empty_transcript_keeps_RedComputes_code()
    {
        var (client, _) = Client(HttpStatusCode.Conflict, new { error = "transcript_empty", message = "Not enough conversation." });

        var result = await client.GenerateSessionTitleAsync("s1", "Update Nova discussion title", Provenance());

        Assert.False(result.Success);
        Assert.Null(result.Title);
        Assert.Equal("transcript_empty", result.ErrorCode);
        Assert.Equal(409, result.StatusCode);
    }

    [Fact]
    public async Task A_failed_generation_keeps_its_message_job_and_status()
    {
        var (client, _) = Client(HttpStatusCode.BadGateway, new { error = "title_generation_failed", message = "The fast model did not return a usable title.", jobId = "job-2" });

        var result = await client.GenerateSessionTitleAsync("s1", "Update Nova discussion title", Provenance());

        Assert.False(result.Success);
        Assert.Equal("title_generation_failed", result.ErrorCode);
        Assert.Equal("The fast model did not return a usable title.", result.Error);
        Assert.Equal("job-2", result.JobId);
        Assert.Equal(502, result.StatusCode);
    }

    [Fact]
    public async Task A_success_without_a_title_is_not_a_title()
    {
        var (client, _) = Client(HttpStatusCode.OK, new { title = "", jobId = "job-3" });

        var result = await client.GenerateSessionTitleAsync("s1", "Update Nova discussion title", Provenance());

        Assert.False(result.Success);
        Assert.Equal("RedCompute returned no title", result.Error);
    }
}
