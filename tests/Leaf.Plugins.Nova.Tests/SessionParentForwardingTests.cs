using Leaf.Sdk;
using Leaf.Sdk.Services;
using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// A session started on an Agent's behalf names the caller's own session (from its verified token), so
/// RedCompute starts it at that session's level, cap and unattended flag instead of the policy default.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    private const string SessionsPath = "/ai-session/sessions";

    [Fact]
    public async Task A_session_started_by_a_brief_or_an_ask_names_the_callers_session()
    {
        using var w = new World(sessionId: null);
        w.Pipeline.BeginSessionCreation(w.Discussion(), ComputeCauses.Agent, "caller-session");
        await w.WaitUntilAsync(() => w.Gateway.SessionsCreated == 1);

        Assert.Equal(["caller-session"], w.Gateway.SessionContextOf(SessionsPath));
    }

    [Fact]
    public async Task A_session_started_by_a_send_names_the_callers_session()
    {
        using var w = new World(sessionId: null);
        await w.Pipeline.SendAsync(w.Discussion(), null, "hello", null,
            new ResolvedDevice { Name = "API", Type = "api", Platform = "api" }, "api",
            callerSessionId: "caller-session");

        Assert.Equal(["caller-session"], w.Gateway.SessionContextOf(SessionsPath));
    }

    [Fact]
    public async Task A_session_nobody_vouches_for_names_no_parent()
    {
        using var w = new World(sessionId: null);
        w.Pipeline.BeginSessionCreation(w.Discussion(), ComputeCauses.Agent);
        await w.WaitUntilAsync(() => w.Gateway.SessionsCreated == 1);

        Assert.Empty(w.Gateway.SessionContextOf(SessionsPath));
    }

    [Fact]
    public async Task A_session_reference_the_caller_already_carries_is_not_added_twice()
    {
        using var w = new World(sessionId: null);
        await w.Pipeline.TryCreateSessionAsync(w.Discussion().AgentId, "owner-1",
            additionalContext: [new ComputeContextReference("session", "from-context")],
            callerSessionId: "caller-session");

        Assert.Equal(["from-context"], w.Gateway.SessionContextOf(SessionsPath));
    }

    // The wiring of each creating route: the id comes from the verified token, never the body.
    [Theory]
    [InlineData("Endpoints/AskEndpoints.cs", "AskAsNoteAsync(discussion")]
    [InlineData("Endpoints/AskEndpoints.cs", "pipeline.SendAsync(")]
    [InlineData("Endpoints/DiscussionEndpoints.cs", "pipeline.BeginSessionCreation(discussion")]
    [InlineData("Endpoints/DiscussionEndpoints.cs", "pipeline.SendInputAsync(")]
    [InlineData("Endpoints/DiscussionEndpoints.cs", "pipeline.SendAsync(")]
    public void Every_route_that_creates_a_session_forwards_the_verified_callers_session(string file, string call)
    {
        var source = File.ReadAllText(FindSource(file));
        var calls = 0;
        for (var at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(call, at + 1, StringComparison.Ordinal))
        {
            calls++;
            var statement = source[at..source.IndexOf(';', at)];
            Assert.Contains("DelegateEndpoints.CallerSessionId(ctx.User)", statement, StringComparison.Ordinal);
        }
        Assert.True(calls >= 1, $"{file} no longer calls {call}");
    }

    [Fact]
    public void A_rotated_LIVE_never_inherits_the_session_that_rotated_it()
    {
        // An automation rotating LIVE would otherwise leave the person's main discussion unattended
        // and capped for good: its level comes from the policy.
        var source = File.ReadAllText(FindSource("Endpoints/DiscussionEndpoints.cs"));
        foreach (var call in new[] { "pipeline.BeginSessionCreation(prior", "pipeline.BeginSessionCreation(fresh" })
        {
            var at = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at >= 0, call);
            Assert.DoesNotContain("CallerSessionId", source[at..source.IndexOf(';', at)], StringComparison.Ordinal);
        }
    }

    private static string FindSource(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Leaf.Plugins.Nova", relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
