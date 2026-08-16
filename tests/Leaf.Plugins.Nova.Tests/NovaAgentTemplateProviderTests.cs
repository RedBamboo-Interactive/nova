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
        Assert.StartsWith("1.0.0-draft.", template.TemplateVersion);
        Assert.Equal("Nova", template.Name);
        Assert.Contains("organizer", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("engineer", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cute", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Laurent", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cyberpunk", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", Combined(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", template.Identity, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, template.DigestSha256.Length);
        Assert.All(template.DigestSha256, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void LoadingTemplateIsDeterministicAndSideEffectFree()
    {
        var first = new NovaAgentTemplateProvider().Template;
        var second = new NovaAgentTemplateProvider().Template;

        Assert.Equal(first, second);
        Assert.Equal(first.DigestSha256, second.DigestSha256);
    }

    [Fact]
    public void WelcomeContributorOwnsTheSameTemplateAndLoadsItsVersionedPrompt()
    {
        IAgentWelcomeProvider provider = new NovaAgentWelcomeProvider(
            null!, null!, null!, null!, null!, null!, null!);

        Assert.Equal("nova/default", provider.TemplateId);
    }

    private static string Combined(AgentTemplateDefinition template)
        => string.Join("\n", template.Name, template.Description, template.Identity,
            template.OutputProtocol, template.MemoryInstructions);
}
