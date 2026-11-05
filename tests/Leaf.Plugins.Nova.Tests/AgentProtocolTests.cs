using System.Text.Json.Nodes;
using Leaf.Sdk;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class AgentProtocolTests
{
    private const string ReactionsParagraph =
        "React to messages from time to time, the way a person in a chat would: when something lands, is funny, "
        + "is a good call or is good news, add a reaction alongside your reply (the context block shows how; its "
        + "message attribute is the uid to react to). Not on every message. The person's reactions reach you in "
        + "context and carry no fixed meaning; read them in context. When a reaction or nothing at all says it "
        + "better than words, reply with exactly <nova-no-reply/> and the turn ends with no visible message. Never "
        + "use a reaction or silence in place of answering something personal.";

    [Fact]
    public void Nova_ships_the_reactions_protocol_with_exactly_the_former_template_paragraph()
    {
        var markdown = File.ReadAllText(SeedPath("protocols", "reactions.md")).Replace("\r\n", "\n");
        var lines = markdown.Split('\n');
        Assert.Equal("---", lines[0]);
        var closing = Array.IndexOf(lines, "---", 1);
        Assert.True(closing > 0);
        var frontmatter = lines[1..closing];
        Assert.Contains("name: reactions", frontmatter);
        Assert.Single(frontmatter, line => line.StartsWith("description: ", StringComparison.Ordinal)
            && line.Length > "description: ".Length);
        Assert.Equal(2, frontmatter.Length);

        var body = string.Join('\n', lines[(closing + 1)..]).Trim();
        Assert.Equal(ReactionsParagraph, body);
        Assert.True(body.Length <= AgentProtocols.MaxInstructionLength);
    }

    [Fact]
    public void Template_output_protocol_no_longer_carries_the_reactions_paragraph()
    {
        var template = new NovaAgentTemplateProvider().Template;

        Assert.DoesNotContain(ReactionsParagraph, template.OutputProtocol, StringComparison.Ordinal);
        Assert.DoesNotContain("React to messages", template.OutputProtocol, StringComparison.Ordinal);
        Assert.DoesNotContain("nova-no-reply", template.OutputProtocol, StringComparison.Ordinal);
        Assert.Contains("Read what you wrote before sending it.", template.OutputProtocol, StringComparison.Ordinal);
        Assert.Equal("1.2.0-draft.4", template.TemplateVersion);
    }

    [Fact]
    public void Select_keeps_order_and_skips_over_cap_empty_duplicate_and_other_types()
    {
        var second = Protocol("Second", "Second rule.");
        var first = Protocol("First", "First rule.");
        var selected = AgentProtocols.Select([
            second,
            Protocol("Too long", new string('x', AgentProtocols.MaxInstructionLength + 1)),
            Protocol("Empty", "  "),
            Protocol("A Skill", "Skill body.", "agent-skill"),
            first,
            second,
        ]);

        Assert.Equal(["Second", "First"], selected.Select(protocol => protocol.Name));
    }

    [Fact]
    public void Fallback_workspace_renders_protocols_right_after_the_output_protocol()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nova-protocol-workspace-{Guid.NewGuid():N}");
        var agent = new AgentInfo(
            Id: Guid.NewGuid().ToString(),
            Slug: "muse",
            Name: "Muse",
            Description: null,
            AvatarFilename: null,
            WorkspaceId: null,
            Identity: "# Muse",
            OutputProtocol: "Own protocol text.",
            Capabilities: "Create art direction.",
            MemoryInstructions: null,
            Protocols: [new("Beta", "Beta rule."), new("Alpha", "Alpha rule.")]);

        try
        {
            var workspace = AgentWorkspace.CreateDisposable(root, agent);
            workspace.GenerateClaudeMd();

            var content = File.ReadAllText(Path.Combine(root, "CLAUDE.md")).Replace("\r\n", "\n");
            var own = content.IndexOf("Own protocol text.", StringComparison.Ordinal);
            var beta = content.IndexOf("## Beta\nBeta rule.", StringComparison.Ordinal);
            var alpha = content.IndexOf("## Alpha\nAlpha rule.", StringComparison.Ordinal);
            var capabilities = content.IndexOf("# Capabilities", StringComparison.Ordinal);
            Assert.True(own >= 0 && own < beta && beta < alpha && alpha < capabilities, content);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LeafEntity Protocol(string name, string instructions, string type = AgentProtocols.EntityType)
        => new(Guid.NewGuid(), type, name.ToLowerInvariant().Replace(' ', '-'), name,
            new JsonObject { ["description"] = "d", ["instructions"] = instructions },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "plugin:nova");

    private static string SeedPath(params string[] parts)
        => Path.GetFullPath(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", "seeds", .. parts]));
}
