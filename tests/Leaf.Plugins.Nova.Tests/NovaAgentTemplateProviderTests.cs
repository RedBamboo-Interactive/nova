using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class NovaAgentTemplateProviderTests
{
    [Fact]
    public void LoadsPortableVersionedTemplateWithoutInstallationBindings()
    {
        IAgentTemplateProvider provider = new NovaAgentTemplateProvider();
        var template = provider.Template;

        Assert.Equal("nova/default", template.Id);
        Assert.Equal(1, template.SchemaVersion);
        Assert.StartsWith("1.1.0-draft.", template.TemplateVersion);
        Assert.Equal("Nova", template.Name);
        Assert.Contains("organizer", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engineer", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cute", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Laurent", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cyberpunk", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([
            "nova/leaf-foundations",
            "nova/leaf-engineering",
            "nova/embed-redleaf-entity",
        ], template.DefaultSkillIds);
        Assert.Equal(64, template.DigestSha256.Length);
        Assert.All(template.DigestSha256, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void LoadingTemplateIsDeterministicAndSideEffectFree()
    {
        var first = new NovaAgentTemplateProvider().Template;
        var second = new NovaAgentTemplateProvider().Template;

        Assert.Equal(first with { DefaultSkillIds = [] }, second with { DefaultSkillIds = [] });
        Assert.Equal(first.DefaultSkillIds, second.DefaultSkillIds);
        Assert.Equal(first.DigestSha256, second.DigestSha256);
    }

    [Fact]
    public void WelcomeContributorOwnsTheSameTemplateAndLoadsItsVersionedPrompt()
    {
        IAgentWelcomeProvider provider = new NovaAgentWelcomeProvider(
            null!, null!, null!, null!, null!, null!);

        Assert.Equal("nova/default", provider.TemplateId);

        var firstRun = NovaAgentWelcomeProvider.PromptFor(AgentWelcomePurpose.FirstRun, "Laurent");
        var review = NovaAgentWelcomeProvider.PromptFor(AgentWelcomePurpose.ReviewExistingAgent);
        Assert.Contains("just created", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Meet Nova", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first visible message", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace identity", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("memory", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Complete setup", firstRun, StringComparison.Ordinal);
        Assert.Contains("Laurent", firstRun, StringComparison.Ordinal);
        Assert.DoesNotContain("Invite the person to tell you their name", firstRun, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuation", review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Meet Nova", review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("memory", review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Complete setup", review, StringComparison.Ordinal);
        Assert.DoesNotContain("tell you their name", review, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WelcomeDiscussionMustRemainBoundToTheRequestedAgentAndWorkspaceSettings()
    {
        var agentId = Guid.NewGuid();
        var context = new AgentWelcomeContext(
            agentId,
            "owner",
            "codex-default",
            "standard",
            AgentWelcomePurpose.ReviewExistingAgent,
            "welcome-key");
        var discussion = new DiscussionRead(
            "discussion", null, null, "idle",
            DateTime.UtcNow, DateTime.UtcNow, 0, null, "owner", Guid.NewGuid(),
            agentId.ToString(), QualityTier: "standard", Provider: "codex-default");

        NovaAgentWelcomeProvider.ValidateDiscussionBinding(discussion, context);

        var wrongAgent = discussion with { AgentId = Guid.NewGuid().ToString() };
        Assert.Throws<InvalidOperationException>(() =>
            NovaAgentWelcomeProvider.ValidateDiscussionBinding(wrongAgent, context));
        var wrongOwner = discussion with { OwnerId = "someone-else" };
        Assert.Throws<InvalidOperationException>(() =>
            NovaAgentWelcomeProvider.ValidateDiscussionBinding(wrongOwner, context));
        var wrongProvider = discussion with { Provider = "opencode-default" };
        Assert.Throws<InvalidOperationException>(() =>
            NovaAgentWelcomeProvider.ValidateDiscussionBinding(wrongProvider, context));
        var wrongTier = discussion with { QualityTier = "fast" };
        Assert.Throws<InvalidOperationException>(() =>
            NovaAgentWelcomeProvider.ValidateDiscussionBinding(wrongTier, context));
    }

    private static string Combined(AgentTemplateDefinition template)
        => string.Join("\n", template.Name, template.Description, template.Identity,
            template.OutputProtocol, template.MemoryInstructions);
}
