using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Xunit;
using Route = Leaf.Plugins.Nova.Tests.PlacementRules.Route;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Placement by reference, stage A: a <c>placement</c> Nova builds never fails a start that works today.
/// The mirror of RedCompute's parser is pinned to R1's own cases (redcompute PlacementRequestTests), and
/// each builder either produces a placement the mirror accepts or none at all.
/// </summary>
public sealed class PlacementRulesTests
{
    private const string AgentId = "7e1e0000-0000-4000-8000-0000000000a1";

    // ── the mirror agrees with RedCompute's PlacementRequestTests (R1) ──────────────────────────

    [Theory]
    [InlineData("""{"projectPath":"C:\\p"}""", Route.Session, null)]
    [InlineData("""{"placement":null}""", Route.Session, null)]
    [InlineData("""{"placement":"local"}""", Route.Session, "placement")]
    [InlineData("""{"placement":{"node":"local"}}""", Route.Session, "placement.node")]
    [InlineData("""{"placement":{"nodeId":7}}""", Route.Session, "placement.nodeId")]
    [InlineData("""{"placement":{"nodeId":" "}}""", Route.Session, "placement.nodeId")]
    [InlineData("""{"placement":{"workspace":"nova"}}""", Route.Session, "placement.workspace")]
    [InlineData("""{"placement":{"workspace":{}}}""", Route.Session, "placement.workspace.agentId")]
    [InlineData("""{"placement":{"workspace":{"agentId":"nova"}}}""", Route.Session, "placement.workspace.agentId")]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1","path":"C:\\x"}}}""", Route.Session, "placement.workspace.path")]
    [InlineData("""{"placement":{"repository":{"branch":"main"}}}""", Route.Session, "placement.repository.repositoryId")]
    [InlineData("""{"placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","checkoutId":"c1"}}}""", Route.Session, "placement.repository.checkoutId")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova"}}}""", Route.Session, "placement.scratch.executionKey")]
    [InlineData("""{"placement":{"scratch":{"owner":"","executionKey":"k"}}}""", Route.Session, "placement.scratch.owner")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"a\u0000b"}}}""", Route.Session, "placement.scratch.executionKey")]
    [InlineData("""{"placement":{"scratchAs":"cwd"}}""", Route.Session, "placement.scratchAs")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"tmp"}}""", Route.Session, "placement.scratchAs")]
    [InlineData("""{"placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","subPath":"../outside"}}}""", Route.Session, "placement.repository.subPath")]
    [InlineData("""{"placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","subPath":"inner/repo"}}}""", Route.Session, null)]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}}""", Route.Session, "placement")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"add-dir"}}""", Route.Session, "placement.scratchAs")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"add-dir"}}""", Route.Execute, null)]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"cwd"}}""", Route.Execute, "placement.scratchAs")]
    [InlineData("""{"placement":{"nodeId":"local"}}""", Route.Session, null)]
    [InlineData("""{"placement":{"nodeId":"LOCAL"}}""", Route.Session, null)]
    [InlineData("""{"placement":{"nodeId":null}}""", Route.Session, null)]
    [InlineData("""{"placement":{"nodeId":"node_01JCZ000000000000000OTHER"}}""", Route.Session, "placement.nodeId")]
    public void The_mirror_refuses_what_RedCompute_refuses(string body, Route route, string? member)
        => Assert.Equal(member, PlacementRules.Problem(JsonNode.Parse(body), route));

    [Theory]
    [InlineData("""{"placement":{"nodeId":"local"}}""", null)]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}}""", "placement.workspace")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"}}}""", "placement.scratch")]
    public void Conversation_only_sessions_take_a_node_only(string body, string? member)
        => Assert.Equal(member, PlacementRules.Problem(JsonNode.Parse(body), Route.Session, conversationOnly: true));

    // ── every builder: a placement RedCompute accepts, or none ──────────────────────────────────

    [Fact]
    public void A_discussion_session_placement_is_accepted_on_the_session_route()
    {
        var placement = ComputePlacement.ForAgent(AgentId, "Nova", "discussion-1");

        Assert.Equal("""{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"Nova","executionKey":"discussion-1"}}""",
            placement!.ToJsonString());
        Assert.Null(PlacementRules.Problem(Body(placement), Route.Session));
    }

    [Fact]
    public void An_automation_placement_adds_the_scratch_directory_and_is_accepted_on_execute_only()
    {
        var placement = ComputePlacement.ForAgent(AgentId, "Nova", "7e1e00000000400080000000000000b1",
            ComputePlacement.ScratchAsAddDir);

        Assert.Equal("add-dir", placement!["scratchAs"]!.GetValue<string>());
        Assert.Null(PlacementRules.Problem(Body(placement), Route.Execute));
        Assert.Equal("placement.scratchAs", PlacementRules.Problem(Body(placement), Route.Session));
    }

    [Theory]
    [InlineData(null, "laurent/x")]
    [InlineData("feature/new", "feature/new")]
    public void A_delegation_placement_is_accepted_on_the_session_route(string? requested, string? resolved)
    {
        var repository = Repository();
        var placement = DelegateEndpoints.DelegatePlacement(repository, requested, resolved, workspaceAgentId: null);

        Assert.Null(PlacementRules.Problem(Body(placement!), Route.Session));
        Assert.Equal(requested is null ? null : resolved, placement!["repository"]!["branch"]?.GetValue<string>());
        var workspace = DelegateEndpoints.DelegatePlacement(null, null, null, AgentId);
        Assert.Null(PlacementRules.Problem(Body(workspace!), Route.Session));
    }

    [Theory]
    [InlineData("nova", "Nova", "k")]
    [InlineData("00000000-0000-0000-0000-000000000000", "Nova", "k")]
    [InlineData(null, "Nova", "k")]
    [InlineData(AgentId, "", "k")]
    [InlineData(AgentId, "  ", "k")]
    [InlineData(AgentId, "No\nva", "k")]
    [InlineData(AgentId, "Nova", "")]
    [InlineData(AgentId, "Nova", null)]
    [InlineData(AgentId, "Nova", "a\u0007b")]
    public void A_part_that_RedCompute_would_refuse_means_no_placement_at_all(string? agentId, string? owner, string? key)
    {
        Assert.Null(ComputePlacement.ForAgent(agentId, owner, key));
        Assert.Null(ComputePlacement.ForAgent(agentId, owner, key, ComputePlacement.ScratchAsAddDir));
    }

    [Fact]
    public void Text_past_RedComputes_limit_means_no_placement_at_all()
    {
        Assert.NotNull(ComputePlacement.ForAgent(AgentId, "Nova", new string('k', 512)));
        Assert.Null(ComputePlacement.ForAgent(AgentId, "Nova", new string('k', 513)));
        Assert.Null(ComputePlacement.ForAgent(AgentId, new string('n', 513), "k"));
    }

    [Fact]
    public void A_branch_Code_did_not_name_means_no_placement_rather_than_the_checkout()
    {
        Assert.Null(DelegateEndpoints.DelegatePlacement(Repository(), "laurent/x", null, null));
        Assert.Null(DelegateEndpoints.DelegatePlacement(Repository(), "laurent/x", " ", null));
        Assert.Null(DelegateEndpoints.DelegatePlacement(null, null, null, "nova"));
        Assert.Null(DelegateEndpoints.DelegatePlacement(null, null, null, null));
    }

    [Fact]
    public void A_null_placement_adds_nothing_to_the_body()
    {
        var body = new Dictionary<string, object?> { ["projectPath"] = "C:\\p" };
        ComputePlacement.AddTo(body, null);
        Assert.Equal(["projectPath"], body.Keys);
    }

    private static JsonObject Body(JsonObject placement) => new() { ["placement"] = placement.DeepClone() };

    private static LeafEntity Repository() => new(Guid.Parse("7e1e0000-0000-4000-8000-000000000001"), "repository",
        "songes", "songes-carmins", new JsonObject { ["status"] = "active" }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "system");
}
