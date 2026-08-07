using System.Collections.Concurrent;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// On-disk workspace access for one agent: config/memory markdown, the workspace
/// manifest, and CLAUDE.md/AGENTS.md generation before a session starts. The agent
/// SYSTEM (entities, VFS mounts) is kernel-level — this only touches the same
/// directories the sessions themselves work in.
/// </summary>
public sealed class AgentWorkspace(string workspacePath)
{
    public string WorkspacePath => workspacePath;
    public string ConfigPath => Path.Combine(workspacePath, "config");
    public string MemoryPath => Path.Combine(workspacePath, "memory");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigPath);
        foreach (var sub in new[] { "conversations", "topics", "meta", "backup", "projects", "dreaming" })
            Directory.CreateDirectory(Path.Combine(MemoryPath, sub));
    }

    public string ReadConfigFile(string name)
    {
        var path = Path.Combine(ConfigPath, name);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Write the agent-entity fields down as config files (identity, protocol, …).</summary>
    public void MaterializeAgentFiles(AgentInfo agent)
    {
        WriteIf("identity.md", agent.Identity);
        WriteIf("output_protocol.md", agent.OutputProtocol);
        WriteIf("capabilities.md", agent.Capabilities);
        WriteIf("memory.md", agent.MemoryInstructions);

        void WriteIf(string name, string? content)
        {
            if (!string.IsNullOrEmpty(content))
                File.WriteAllText(Path.Combine(ConfigPath, name), content);
        }
    }

    public string[] GetManifest()
    {
        if (!Directory.Exists(workspacePath)) return [];

        var backupPath = Path.Combine(MemoryPath, "backup");
        var rootExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLAUDE.md", "AGENTS.md" };

        return Directory.GetFiles(workspacePath, "*.md", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(backupPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(workspacePath, p).Replace('\\', '/'))
            .Where(p => !rootExcludes.Contains(p))
            .OrderBy(p => p)
            .ToArray();
    }

    public string? ReadFile(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));
        if (!fullPath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    public void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));
        if (!fullPath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes the agent workspace");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void GenerateClaudeMd()
    {
        var identity = ReadConfigFile("identity.md");
        var protocol = ReadConfigFile("output_protocol.md");
        var capabilities = ReadConfigFile("capabilities.md");
        var memoryInstructions = ReadConfigFile("memory.md");
        var manifestList = string.Join("\n", GetManifest().Select(p => $"- {p}"));

        var content = $"""
            > **This file is generated. Do not edit it directly.**
            > To change these instructions, edit the source files in `config/`.
            > Sections: identity.md, output_protocol.md, capabilities.md, memory.md

            # Session Scratch Space

            `REDLEAF_SCRATCH_DIR` points to the current session's disposable workspace. Put downloads,
            probes, generated intermediates, and temporary scripts there. Do not create `temp`, `tmp`,
            or `scratch` folders inside the persistent Agent workspace. Promote only deliberate final
            work into the persistent workspace.

            {identity}

            ---

            # Output Protocol
            {protocol}

            ---

            # Capabilities
            {capabilities}

            ---

            {memoryInstructions}

            ## Current files
            {manifestList}
            """;

        foreach (var name in new[] { "CLAUDE.md", "AGENTS.md" })
            File.WriteAllText(Path.Combine(workspacePath, name), content);
    }
}

/// <summary>Per-agent workspace cache; materializes agent-entity config files on first use.</summary>
public sealed class AgentWorkspaces(
    AgentDirectory agents,
    IAgentWorkspacePathResolver workspacePaths)
{
    private readonly ConcurrentDictionary<string, AgentWorkspace> _cache = new();

    public async Task<AgentWorkspace> GetAsync(string? agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("An Agent must be selected");
        if (_cache.TryGetValue(agentId, out var cached))
            return cached;

        var agent = await agents.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent not found: {agentId}");
        if (!Guid.TryParse(agent.Id, out var entityId))
            throw new InvalidOperationException($"Agent has an invalid entity id: {agent.Id}");
        var path = await workspacePaths.ResolveAsync(entityId, ct)
            ?? throw new InvalidOperationException(
                $"The entity-backed workspace for Agent '{agent.Name}' is unavailable");

        var workspace = new AgentWorkspace(path);
        workspace.EnsureDirectories();
        workspace.MaterializeAgentFiles(agent);

        _cache[agentId] = workspace;
        return workspace;
    }

    public void Invalidate(string? agentId = null)
    {
        if (agentId != null) _cache.TryRemove(agentId, out _);
        else _cache.Clear();
    }
}
