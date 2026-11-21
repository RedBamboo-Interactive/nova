using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// Nova keeps nothing of its own on disk (its state is entities); the one place it built a RedLeaf path was the
/// share enricher reading an agent's avatar out of the asset folder. That now goes through IAssets, so nothing in the
/// plugin depends on where RedLeaf's data lives.
/// </summary>
public sealed class NovaStoragePathTests
{
    [Theory]
    [InlineData("avatar.png", "avatar.png")]
    [InlineData("/api/assets/avatar.webp", "avatar.webp")]
    [InlineData("/api/assets/", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("https://example.test/a.png", null)]
    public void An_avatar_filename_resolves_to_an_asset_id_never_to_a_path(string filename, string? expected)
    {
        Assert.Equal(expected, NovaShareEnricher.ResolveAvatarAssetId(filename));
    }

    [Fact]
    public void No_plugin_source_builds_a_path_under_LocalApplicationData()
    {
        var directory = RepositoryRoot();
        Assert.True(Directory.Exists(Path.Combine(directory.FullName, "src", "Leaf.Plugins.Nova")));

        var offenders = Directory
            .EnumerateFiles(Path.Combine(directory.FullName, "src", "Leaf.Plugins.Nova"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(directory.FullName, f))
            .ToList();

        Assert.Empty(offenders);
    }

    // From the test output directory, not [CallerFilePath]: a release build maps source paths to /_/ (PathMap).
    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "plugin.json")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("Could not locate the Nova repository root.");
    }
}
