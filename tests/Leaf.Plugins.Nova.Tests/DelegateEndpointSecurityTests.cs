using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DelegateEndpointSecurityTests
{
    [Fact]
    public void Rejects_unauthenticated_and_local_default_callers()
    {
        Assert.Null(DelegateEndpoints.TrustedCallerId(new ClaimsPrincipal()));
        Assert.Null(DelegateEndpoints.TrustedCallerId(Principal("local-user")));
    }

    [Theory]
    [InlineData("28a07bf5-3a37-47d4-a9ab-bdc09221d547")]
    [InlineData("system")]
    [InlineData("service:nova")]
    public void Accepts_explicit_authenticated_subjects(string subject)
    {
        Assert.Equal(subject, DelegateEndpoints.TrustedCallerId(Principal(subject)));
    }

    [Fact]
    public void Classifies_execution_identity_failures_without_an_apphost_dependency()
    {
        Assert.True(DelegateEndpoints.IsExecutionIdentityFailure(
            new ExecutionIdentityValidationException("rejected")));
        Assert.False(DelegateEndpoints.IsExecutionIdentityFailure(
            new InvalidOperationException("unrelated")));
    }

    [Theory]
    [InlineData("{\"accepted\":true,\"disposition\":\"queued\"}", true)]
    [InlineData("{\"accepted\":true,\"disposition\":\"delivered\"}", true)]
    [InlineData("{\"sent\":true}", true)]
    [InlineData("{\"accepted\":false}", false)]
    [InlineData("{\"error\":\"rejected\"}", false)]
    public void Recognizes_durable_queue_and_legacy_prompt_acceptance(
        string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected,
            DelegateEndpoints.IsPromptAccepted(document.RootElement.Clone()));
    }

    [Fact]
    public async Task Session_client_preserves_execution_identity_failure_semantics()
    {
        var client = new RedComputeClient(new ThrowingComputeGateway(
            new ExecutionIdentityValidationException("beneficiary mismatch")));

        var result = await client.SendMessageDetailedAsync(
            "session-1", new { content = "test" }, Provenance());

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("execution_identity_rejected", result.ErrorCode);
        Assert.Equal("beneficiary mismatch", result.ErrorMessage);
    }

    [Fact]
    public void Resolves_legacy_project_path_by_exact_normalized_active_repository_path()
    {
        var expected = Repository("active", "T:/Projects/Nova");
        var inactive = Repository("inactive", "T:/Projects/Nova");
        var nested = Repository("active", "T:/Projects/Nova/child");

        var matches = DelegateEndpoints.FindMatchingActiveRepositories(
            [inactive, nested, expected],
            @"t:\projects\nova\");

        Assert.Equal([expected.Id], matches.Select(repository => repository.Id));
    }

    [Fact]
    public void Does_not_treat_nested_repository_path_as_an_exact_match()
    {
        var matches = DelegateEndpoints.FindMatchingActiveRepositories(
            [Repository("active", "T:/Projects/Nova")],
            "T:/Projects/Nova/child");

        Assert.Empty(matches);
    }

    private static ClaimsPrincipal Principal(string subject) => new(
        new ClaimsIdentity([new Claim("sub", subject)], "test"));

    private static LeafEntity Repository(string status, string path) => new(
        Guid.NewGuid(),
        "repository",
        $"repository-{Guid.NewGuid():N}",
        "Repository",
        new JsonObject
        {
            ["status"] = status,
            ["local_path"] = path,
        },
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "system");

    private static ComputeProvenance Provenance() => new(
        ComputeProvenance.CurrentSchemaVersion,
        new ComputeOrigin("redleaf",
            new ComputeAppReference("plugin", "nova", null, "Nova"),
            new ComputeEntrypoint("http", "/api/apps/nova/delegate", "POST")),
        new ComputeActor("agent", "Nova", Id: "nova"),
        new ComputeBeneficiary("user", "user-1", "Laurent"),
        [], new ComputeTrace(), ComputeProvenanceAssurance.Verified,
        DateTimeOffset.UtcNow);

    private sealed class ThrowingComputeGateway(Exception exception) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            ComputeProvenance? provenance = null, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class ExecutionIdentityValidationException(string message)
        : Exception(message);
}
