using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class EmbedRedLeafEntityResolverTests
{
    [Fact]
    public void Resolver_refuses_an_origin_override_before_making_a_request()
    {
        var result = RunResolver(
            ["--query", "Standard", "--base-url", "http://127.0.0.1:19999"],
            executionToken: "must-not-be-forwarded");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("error", result.Status);
        Assert.Contains("Unexpected argument: --base-url", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_reports_the_promised_status_when_execution_identity_is_missing()
    {
        var result = RunResolver(["--query", "Standard"], executionToken: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("authentication_required", result.Status);
        Assert.Equal("REDLEAF_EXECUTION_TOKEN is required", result.Error);
    }

    private static ResolverResult RunResolver(
        IReadOnlyList<string> arguments,
        string? executionToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ResolverPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (executionToken is null)
            start.Environment.Remove("REDLEAF_EXECUTION_TOKEN");
        else
            start.Environment["REDLEAF_EXECUTION_TOKEN"] = executionToken;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the bundled entity resolver.");
        var output = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(output.Length > 0, $"Resolver emitted no JSON. stderr: {standardError}");

        using var json = JsonDocument.Parse(output);
        return new ResolverResult(
            process.ExitCode,
            json.RootElement.GetProperty("status").GetString() ?? "",
            json.RootElement.GetProperty("error").GetString() ?? "");
    }

    private static string ResolverPath()
        => Path.Combine(
            RepositoryRoot(),
            "seeds",
            "skills",
            "embed-redleaf-entity",
            "scripts",
            "find-entity.cjs");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "plugin.json")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Nova repository root.");
    }

    private sealed record ResolverResult(int ExitCode, string Status, string Error);
}
