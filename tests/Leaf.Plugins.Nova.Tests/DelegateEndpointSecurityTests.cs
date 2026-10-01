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

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Omitted_or_false_navigation_never_returns_a_route(bool? navigate)
    {
        Assert.Null(DelegateEndpoints.RequestedNavigationPath(
            Principal("user-1"), navigate, "session-1"));
    }

    [Fact]
    public void Explicit_human_navigation_returns_a_client_local_route()
    {
        Assert.Equal("/apps/codered/sessions/session%2F1",
            DelegateEndpoints.RequestedNavigationPath(
                Principal("user-1"), true, "session/1"));
    }

    [Fact]
    public void Agent_execution_never_receives_a_navigation_route()
    {
        var principal = Principal("user-1", new Claim("token_use", "execution"));

        Assert.Null(DelegateEndpoints.RequestedNavigationPath(
            principal, true, "session-1"));
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

    [Theory]
    [InlineData(403, false, true)]
    [InlineData(403, true, false)]
    [InlineData(500, false, false)]
    [InlineData(504, false, false)]
    [InlineData(200, false, false)]
    public void Lost_admission_outcome_never_licenses_orphan_cleanup(int code, bool unknown, bool expected)
        => Assert.Equal(expected, DelegateEndpoints.IsAdmissionDefinitivelyRejected(new(code < 400, null, code), unknown));

    [Fact]
    public async Task Exact_prompt_opt_in_is_separate_from_legacy_operation_identity_and_cleanup_needs_receipt()
    {
        var requests = new List<JsonElement>(); var confirmed = false;
        var gateway = CallbackDeliveryTests.Proxy.Create<IComputeGateway>((method, args) => {
            Assert.Equal("SendAsync", method);
            var request = (HttpRequestMessage)args![0]!;
            if (request.Method == HttpMethod.Post)
                requests.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { removed = confirmed })) });
        });
        var client = new RedComputeClient(gateway);
        Assert.True(await client.RegisterCallbackAsync("session", "http://localhost/callback", callbackId: "operation"));
        Assert.False(requests[0].TryGetProperty("promptMessageUid", out _));
        Assert.True(await client.RegisterCallbackAsync("session", "http://localhost/callback", force: true, callbackId: "operation", promptMessageUid: "accepted-prompt"));
        Assert.Equal("operation", requests[1].GetProperty("callbackId").GetString());
        Assert.Equal("accepted-prompt", requests[1].GetProperty("promptMessageUid").GetString());
        Assert.False(await client.RemoveUnacceptedCallbackAsync("session", "operation", "accepted-prompt"));
        confirmed = true;
        Assert.True(await client.RemoveUnacceptedCallbackAsync("session", "operation", "accepted-prompt"));
    }

    private static ClaimsPrincipal Principal(string subject, params Claim[] claims) => new(
        new ClaimsIdentity([new Claim("sub", subject), .. claims], "test"));

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
