using System.Text.Json.Nodes;
using Leaf.Plugins.Nova;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ExtensionArchitectureTests
{
    [Fact]
    public void Nova_declares_generic_backend_slots_and_contains_no_presence_contract()
    {
        var root = FindRoot();
        var manifest = PluginManifest.Load(Path.Combine(root.FullName, "plugin.json"));
        var slots = manifest.Backend!.Slots.ToDictionary(slot => slot.Id);
        Assert.Equal(PluginExtensionContracts.ContextFragmentV1,
            slots[ExtensionContributions.ContextSlot].Contract);
        Assert.Equal(PluginExtensionContracts.LiveEventV1,
            slots[ExtensionContributions.LiveSlot].Contract);

        var production = string.Join('\n', Directory.GetFiles(
            Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("presence-state", production, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-installation", production, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("presence.current", production, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresenceReader", production, StringComparison.Ordinal);
        Assert.DoesNotContain("/extensions/presence", production, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", production, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", production, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_extension_context_is_rendered_without_domain_knowledge()
    {
        var fragment = new PluginContextFragment(
            "current", "test-extension", "A composed answer.",
            new JsonObject { ["revision"] = 4 }, Revision: "4");
        var snapshot = NovaContextBuilder.BuildSnapshot([], null, null, null,
            extensionContexts: [fragment]);
        var rendered = NovaContextBuilder.BuildFullContext(snapshot, "discussion",
            DateTime.UtcNow,
            new ResolvedDevice { Name = "API", Type = "api", Platform = "api" },
            "api", "Nova");

        Assert.Contains("Installed extension context:", rendered);
        Assert.Contains("[test-extension] A composed answer.", rendered);
    }

    private static DirectoryInfo FindRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "plugin.json")))
            root = root.Parent;
        return root ?? throw new DirectoryNotFoundException("Could not find Nova plugin root");
    }
}
