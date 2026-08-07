using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public class NovaAgentEntityDisplayEnricherTests
{
    [Fact]
    public void Project_UsesTheEffectiveAvatarAsACircularAgentVisual()
    {
        var agent = new AgentInfo(
            Id: Guid.NewGuid().ToString(),
            Slug: "nova",
            Name: "Nova",
            Description: "AI assistant with persistent identity and memory",
            AvatarFilename: "active-outfit.png",
            WorkspaceId: null,
            Identity: null,
            OutputProtocol: null,
            Capabilities: null,
            MemoryInstructions: null);

        var projection = NovaAgentEntityDisplayEnricher.Project(agent);

        Assert.NotNull(projection);
        Assert.Equal("AI assistant with persistent identity and memory", projection.Subtitle);
        Assert.Equal("/api/assets/active-outfit.png", projection.ImageUrl);
        Assert.Equal("circle", projection.ImageShape);
    }

    [Fact]
    public void Project_PreservesAbsoluteAndSameOriginAvatarUrls()
    {
        var absolute = MakeAgent("https://cdn.example/avatar.png");
        var sameOrigin = MakeAgent("/api/assets/avatar.png");

        Assert.Equal("https://cdn.example/avatar.png",
            NovaAgentEntityDisplayEnricher.Project(absolute)!.ImageUrl);
        Assert.Equal("/api/assets/avatar.png",
            NovaAgentEntityDisplayEnricher.Project(sameOrigin)!.ImageUrl);
    }

    private static AgentInfo MakeAgent(string avatar) => new(
        Guid.NewGuid().ToString(), "nova", "Nova", null, avatar,
        null, null, null, null, null);
}
