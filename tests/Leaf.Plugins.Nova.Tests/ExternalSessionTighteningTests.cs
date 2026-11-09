using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Discord and Leaf Community sessions created before they started unattended and capped at Auto
/// are brought up to that once, before their next turn, and only ever tightened.
/// </summary>
public sealed partial class ExternalAgentConversationProviderTests
{
    private const string TightenPath = "/ai-session/sessions/session-1/access-level";
    private const string LegacySession = """{"session":{"status":"Idle","providerSessionId":"thread-1","accessLevel":"full"}}""";

    [Theory]
    [InlineData("discord", "Idle")]
    [InlineData("discord", "Stopped")]
    [InlineData("leaf-commons", "Idle")]
    public async Task A_legacy_session_is_tightened_before_its_turn(string transport, string status)
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(
            LegacySession.Replace("\"Idle\"", $"\"{status}\"", StringComparison.Ordinal),
            transport);

        var admission = await provider.SendAsync(handle, TighteningInput(agentId, transport));

        Assert.Equal("delivered", admission.Disposition);
        var tighten = gateway.Requests.FindIndex(request => request.Path == TightenPath);
        var message = gateway.Requests.FindIndex(request => request.Path == "/ai-session/sessions/session-1/message");
        Assert.True(tighten >= 0);
        Assert.True(message > tighten);
        if (status == "Stopped")
        {
            var resume = gateway.Requests.FindIndex(request => request.Path == "/ai-session/sessions/session-1/resume");
            Assert.True(resume > tighten, "the stopped session relaunches under its cap");
        }
        using var body = JsonDocument.Parse(gateway.Requests[tighten].Body);
        Assert.Equal("auto", body.RootElement.GetProperty("maxAccessLevel").GetString());
        Assert.True(body.RootElement.GetProperty("unattended").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("level", out _));
        Assert.False(body.RootElement.TryGetProperty("accessLevel", out _));
    }

    [Fact]
    public async Task A_live_session_stopped_by_its_tightening_is_resumed_before_its_turn()
    {
        // RedCompute stops a live harness that will not switch down to the new cap.
        var (provider, gateway, handle, agentId, _) = TighteningHarness(LegacySession);
        gateway.AfterTightenBody =
            """{"session":{"status":"Stopped","providerSessionId":"thread-1","accessLevel":"auto","maxAccessLevel":"auto","unattended":true}}""";

        var admission = await provider.SendAsync(handle, TighteningInput(agentId));

        Assert.Equal("delivered", admission.Disposition);
        var tighten = gateway.Requests.FindIndex(request => request.Path == TightenPath);
        var resume = gateway.Requests.FindIndex(request => request.Path == "/ai-session/sessions/session-1/resume");
        var message = gateway.Requests.FindIndex(request => request.Path == "/ai-session/sessions/session-1/message");
        Assert.True(tighten >= 0);
        Assert.True(resume > tighten);
        Assert.True(message > resume);
    }

    [Fact]
    public async Task An_already_capped_unattended_session_is_left_alone()
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(
            """{"session":{"status":"Idle","providerSessionId":"thread-1","accessLevel":"auto","maxAccessLevel":"auto","unattended":true}}""");

        await provider.SendAsync(handle, TighteningInput(agentId));

        Assert.DoesNotContain(gateway.Requests, request => request.Path == TightenPath);
        Assert.Contains(gateway.Requests, request => request.Path == "/ai-session/sessions/session-1/message");
    }

    [Fact]
    public async Task A_cap_below_auto_is_never_raised_and_only_unattended_is_sent()
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(
            """{"session":{"status":"Idle","providerSessionId":"thread-1","accessLevel":"ask","maxAccessLevel":"ask"}}""");

        await provider.SendAsync(handle, TighteningInput(agentId));

        var tighten = Assert.Single(gateway.Requests, request => request.Path == TightenPath);
        using var body = JsonDocument.Parse(tighten.Body);
        Assert.False(body.RootElement.TryGetProperty("maxAccessLevel", out _));
        Assert.True(body.RootElement.GetProperty("unattended").GetBoolean());
    }

    [Fact]
    public async Task An_unattended_session_capped_at_full_only_has_its_cap_lowered()
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(
            """{"session":{"status":"Idle","providerSessionId":"thread-1","accessLevel":"full","maxAccessLevel":"full","unattended":true}}""");

        await provider.SendAsync(handle, TighteningInput(agentId));

        var tighten = Assert.Single(gateway.Requests, request => request.Path == TightenPath);
        using var body = JsonDocument.Parse(tighten.Body);
        Assert.Equal("auto", body.RootElement.GetProperty("maxAccessLevel").GetString());
        Assert.False(body.RootElement.TryGetProperty("unattended", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task A_tightening_failure_still_delivers_the_turn(HttpStatusCode tightenStatus, bool throws)
    {
        var (provider, gateway, handle, agentId, logs) = TighteningHarness(LegacySession);
        gateway.TightenStatus = tightenStatus;
        gateway.TightenThrows = throws;

        var admission = await provider.SendAsync(handle, TighteningInput(agentId));

        Assert.Equal("delivered", admission.Disposition);
        Assert.Contains(gateway.Requests, request => request.Path == TightenPath);
        Assert.Contains(gateway.Requests, request => request.Path == "/ai-session/sessions/session-1/message");
        Assert.Contains(logs, line => line.StartsWith("Warning: Could not tighten existing discord session session-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_tightening_is_tried_again_and_a_successful_one_is_not_repeated()
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(LegacySession);
        gateway.TightenStatus = HttpStatusCode.ServiceUnavailable;

        await provider.SendAsync(handle, TighteningInput(agentId, requestId: "discord-message-1"));
        gateway.TightenStatus = HttpStatusCode.OK;
        await provider.SendAsync(handle, TighteningInput(agentId, requestId: "discord-message-2"));
        // RedCompute still reporting the old fields (a stale read) does not make it try again.
        await provider.SendAsync(handle, TighteningInput(agentId, requestId: "discord-message-3"));

        Assert.Equal(2, gateway.Requests.Count(request => request.Path == TightenPath));
        Assert.Equal(3, gateway.Requests.Count(request => request.Path == "/ai-session/sessions/session-1/message"));
    }

    [Fact]
    public async Task Other_embedding_apps_are_not_tightened()
    {
        var (provider, gateway, handle, agentId, _) = TighteningHarness(LegacySession, "storyboard");

        await provider.SendAsync(handle, TighteningInput(agentId, "storyboard"));

        Assert.DoesNotContain(gateway.Requests, request => request.Path == TightenPath);
        Assert.Contains(gateway.Requests, request => request.Path == "/ai-session/sessions/session-1/message");
    }

    [Theory]
    [InlineData(null, false, "auto", true)]
    [InlineData("full", false, "auto", true)]
    [InlineData("full", true, "auto", false)]
    [InlineData("auto", false, null, true)]
    [InlineData("ask", false, null, true)]
    [InlineData("plan", false, null, true)]
    [InlineData("bogus", true, "auto", false)]
    public void Tightening_asks_only_for_what_is_missing(
        string? cap, bool unattended, string? expectedCap, bool expectedUnattended)
    {
        var tightening = ExternalAgentConversationProvider.DiscordTightening(
            new RedComputeClient.SessionProbe(true, "Idle", Unattended: unattended, MaxAccessLevel: cap));

        Assert.Equal((expectedCap, expectedUnattended), tightening);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("ask")]
    [InlineData("plan")]
    [InlineData("AUTO")]
    public void A_capped_unattended_session_needs_nothing(string cap)
        => Assert.Null(ExternalAgentConversationProvider.DiscordTightening(
            new RedComputeClient.SessionProbe(true, "Idle", Unattended: true, MaxAccessLevel: cap)));

    private static (ExternalAgentConversationProvider Provider, TighteningGateway Gateway,
        ExternalConversationHandle Handle, Guid AgentId, List<string> Logs) TighteningHarness(
            string sessionBody, string transport = "discord")
    {
        var agentId = Guid.NewGuid();
        var entities = new FakeEntityStore(
            Entity(agentId, "agent", "nova", "Nova"),
            Entity(Guid.NewGuid(), "plugin", NovaAppPlugin.PluginId, "Nova"));
        var agents = new AgentDirectory(entities, new NoOpPluginEvents());
        var gateway = new TighteningGateway(sessionBody);
        var verifier = new DiscordPromptInjectionVerifier(
            new RecordingSentinel(), agents, entities,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscordPromptInjectionVerifier>.Instance);
        var logs = new List<string>();
        var provider = new ExternalAgentConversationProvider(
            null!, new RedComputeClient(gateway), agents, entities, verifier, new TighteningLogger(logs));
        return (provider, gateway,
            new ExternalConversationHandle("leaf-agent-session", "binding-1", 1, "conversation-1", "session-1"),
            agentId, logs);
    }

    private static ExternalConversationInput TighteningInput(
        Guid agentId, string transport = "discord", string requestId = "discord-message-1")
    {
        var metadata = new JsonObject
        {
            ["agent_id"] = agentId.ToString(),
            ["sentinel_agent_id"] = agentId.ToString(),
            ["binding_id"] = "binding-1",
            ["owner_user_id"] = "owner-1",
            ["transport"] = transport,
            ["application_id"] = transport,
        };
        return new ExternalConversationInput(
            requestId, new ExternalRequestor("user-1", "Laurent"), "hello", [], metadata);
    }

    private sealed class TighteningGateway(string sessionBody) : IComputeGateway
    {
        public List<(string Path, string Body)> Requests { get; } = [];
        public HttpStatusCode TightenStatus { get; set; } = HttpStatusCode.OK;
        public bool TightenThrows { get; set; }
        /// <summary>What a session read returns once a tightening succeeded, when it changed the session.</summary>
        public string? AfterTightenBody { get; set; }
        private bool tightened;

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            ComputeProvenance? provenance = null,
            CancellationToken ct = default)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((path, body));
            if (request.Method == HttpMethod.Get)
                return Response(HttpStatusCode.OK, tightened && AfterTightenBody is not null ? AfterTightenBody : sessionBody);
            if (path == TightenPath)
            {
                if (TightenThrows) throw new HttpRequestException("RedCompute went away");
                tightened = TightenStatus == HttpStatusCode.OK;
                return Response(TightenStatus, TightenStatus == HttpStatusCode.OK
                    ? """{"accessLevel":"auto","ok":true}"""
                    : """{"error":"tightening_failed","message":"could not tighten"}""");
            }
            var content = path == "/ai-session/execute"
                ? """{"success":true,"text":"{\"risk\":\"none\",\"signals\":[],\"handling\":\"normal\"}","sessionId":"sentinel-1"}"""
                : path.EndsWith("/message", StringComparison.Ordinal)
                    ? """{"disposition":"delivered","queueItemId":"queue-1"}"""
                    : "{}";
            return Response(HttpStatusCode.OK, content);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string content)
            => new(status) { Content = new StringContent(content) };
    }

    private sealed class TighteningLogger(List<string> sink) : ILogger<ExternalAgentConversationProvider>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
