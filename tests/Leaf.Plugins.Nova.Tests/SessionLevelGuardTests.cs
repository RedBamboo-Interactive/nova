using System.Net;
using System.Security.Claims;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>A session reaches into another only at or below its own level.</summary>
public sealed class SessionLevelGuardTests
{
    private static ClaimsPrincipal Caller(string? sessionId) => new(new ClaimsIdentity(
        [new Claim("sub", "owner"), new Claim("token_use", "execution"),
         new Claim("execution_identity", sessionId is null ? """{"context":[]}"""
             : $$"""{"context":[{"kind":"session","id":"{{sessionId}}"}]}""")], "Bearer"));

    private static async Task<(int? Status, string? Error)> Run(ClaimsPrincipal user, string? target, Dictionary<string, string?> levels, bool reachable = true)
    {
        var client = new RedComputeClient(new LevelGateway(levels, reachable));
        var refusal = await SessionLevelGuard.RefuseUpwardAsync(user, target, client, default);
        if (refusal is null) return (null, null);
        return (((IStatusCodeHttpResult)refusal).StatusCode,
            System.Text.Json.JsonSerializer.SerializeToElement(((IValueHttpResult)refusal).Value).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("ask", "full", 403)]
    [InlineData("ask", "auto", 403)]
    [InlineData("auto", "full", 403)]
    [InlineData("plan", "ask", 403)]
    public async Task A_session_cannot_post_into_one_above_its_level(string caller, string target, int status)
        => Assert.Equal((status, "session_above_caller"),
            await Run(Caller("caller"), "target", new() { ["caller"] = caller, ["target"] = target }));

    [Theory]
    [InlineData("full", "full")]
    [InlineData("full", "ask")]
    [InlineData("auto", "auto")]
    [InlineData("ask", "plan")]
    public async Task Equal_or_lower_is_unchanged(string caller, string target)
        => Assert.Equal((null, null), await Run(Caller("caller"), "target", new() { ["caller"] = caller, ["target"] = target }));

    [Fact]
    public async Task A_person_a_tokenless_caller_and_a_token_naming_no_session_are_unchanged()
    {
        var levels = new Dictionary<string, string?> { ["target"] = "full" };
        Assert.Equal((null, null), await Run(Caller(null), "target", levels));
        Assert.Equal((null, null), await Run(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "Bearer")), "target", levels));
        Assert.Equal((null, null), await Run(new ClaimsPrincipal(), "target", levels));
    }

    [Fact]
    public async Task Posting_into_its_own_session_or_to_none_is_unchanged()
    {
        var levels = new Dictionary<string, string?> { ["caller"] = "ask" };
        Assert.Equal((null, null), await Run(Caller("caller"), "caller", levels));
        Assert.Equal((null, null), await Run(Caller("caller"), null, levels));
    }

    [Fact]
    public async Task A_session_with_no_recorded_level_counts_as_full_on_both_sides()
    {
        // It predates access levels and ran with everything allowed.
        Assert.Equal((403, "session_above_caller"),
            await Run(Caller("caller"), "target", new() { ["caller"] = "ask", ["target"] = null }));
        Assert.Equal((null, null),
            await Run(Caller("caller"), "target", new() { ["caller"] = null, ["target"] = "full" }));
    }

    [Fact]
    public async Task Unreadable_levels_refuse_rather_than_guess()
        => Assert.Equal((503, "session_level_unverified"),
            await Run(Caller("caller"), "target", new() { ["caller"] = "full", ["target"] = "ask" }, reachable: false));

    [Fact]
    public async Task A_target_RedCompute_no_longer_has_is_left_to_the_send()
        => Assert.Equal((null, null), await Run(Caller("caller"), "gone", new() { ["caller"] = "ask" }));

    // The routes that put words into a session call the guard before anything is sent.
    [Theory]
    [InlineData("Endpoints/DelegateEndpoints.cs", "SessionLevelGuard.RefuseUpwardAsync", "var status = raw.RootElement")]
    [InlineData("Endpoints/DiscussionEndpoints.cs", "SessionLevelGuard.RefuseUpwardAsync", "injector.InjectEventAsync(discussion, request.Content, request.Type")]
    [InlineData("Endpoints/DiscussionEndpoints.cs", "SessionLevelGuard.RefuseUpwardAsync", "store.PostConversationMessageAsync(")]
    [InlineData("Endpoints/AskEndpoints.cs", "SessionLevelGuard.RefuseUpwardAsync", "pipeline.SendAsync(")]
    public void Each_route_into_a_session_checks_the_level_first(string file, string guard, string send)
    {
        var source = File.ReadAllText(FindSource(file));
        var guardAt = source.IndexOf(guard, StringComparison.Ordinal);
        var sendAt = source.IndexOf(send, StringComparison.Ordinal);
        Assert.True(guardAt >= 0 && sendAt >= 0, $"{file}: guard or send not found");
        Assert.True(guardAt < sendAt, $"{file}: the level check must come before {send}");
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

    private sealed class LevelGateway(Dictionary<string, string?> levels, bool reachable) : IComputeGateway
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            if (!reachable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var id = request.RequestUri!.OriginalString.Split('/').Last();
            if (!levels.TryGetValue(id, out var level))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = level is null
                ? """{"session":{"status":"Idle"}}"""
                : "{\"session\":{\"status\":\"Idle\",\"accessLevel\":\"" + level + "\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
