using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class AgentWorkspaceTests
{
    [Fact]
    public void GenerateClaudeMdPlacesScratchConventionInHarnessInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nova-agent-workspace-{Guid.NewGuid():N}");

        try
        {
            var workspace = new AgentWorkspace(root);
            workspace.EnsureDirectories();

            workspace.GenerateClaudeMd();

            foreach (var fileName in new[] { "AGENTS.md", "CLAUDE.md" })
            {
                var content = File.ReadAllText(Path.Combine(root, fileName));
                Assert.Contains("REDLEAF_SCRATCH_DIR", content);
                Assert.Contains("disposable workspace", content);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
