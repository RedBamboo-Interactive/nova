using System.Collections.Concurrent;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// On-disk workspace access for one agent: config/memory markdown, the workspace
/// manifest, and CLAUDE.md/AGENTS.md generation before a session starts. The agent
/// SYSTEM (entities, VFS mounts) is kernel-level — this only touches the same
/// directories the sessions themselves work in.
/// </summary>
public sealed class AgentWorkspace(string workspacePath, bool isDisposable = false)
{
    public string WorkspacePath => workspacePath;
    public string ConfigPath => Path.Combine(workspacePath, "config");
    public string MemoryPath => Path.Combine(workspacePath, "memory");
    public bool IsDisposable => isDisposable;

    public static AgentWorkspace CreateDisposable(string workspacePath, AgentInfo agent)
    {
        var workspace = new AgentWorkspace(workspacePath, isDisposable: true);
        workspace.EnsureDirectories();
        workspace.MaterializeAgentFiles(agent);
        return workspace;
    }

    /// <summary>
    /// True when RedLeaf owns the harness files as a generated, read-only VFS projection. Physical
    /// fallback workspaces remain writable and keep the legacy materialize/compile path.
    /// </summary>
    public bool UsesGeneratedHarnessProjection()
        => new[] { "AGENTS.md", "CLAUDE.md" }.All(name =>
        {
            var path = Path.Combine(workspacePath, name);
            return File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        });

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
        // The canonical VFS compiler already produced these files from the Agent and selected
        // Skills. Writing them would correctly fail with ACCESS_DENIED and would reintroduce a
        // second source of truth. Keep compilation here only for physical fallback workspaces.
        if (UsesGeneratedHarnessProjection()) return;

        var identity = ReadConfigFile("identity.md");
        var protocol = ReadConfigFile("output_protocol.md");
        var capabilities = ReadConfigFile("capabilities.md");
        var memoryInstructions = ReadConfigFile("memory.md");
        var manifestList = string.Join("\n", GetManifest().Select(p => $"- {p}"));

        var storageInstructions = IsDisposable
            ? """
              # Disposable Agent Workspace

              This Agent has no configured entity-backed Workspace. This entire working directory is
              session-scoped scratch storage and may be removed after execution. Conversation history
              remains in the discussion, but files and memory written here do not persist across
              discussions. Do not claim that a file or memory was saved durably.

              `REDLEAF_SCRATCH_DIR` points to this same disposable working directory.
              """
            : """
              # Session Scratch Space

              `REDLEAF_SCRATCH_DIR` points to the current session's disposable workspace. Put downloads,
              probes, generated intermediates, and temporary scripts there. Do not create `temp`, `tmp`,
              or `scratch` folders inside the persistent Agent workspace. Promote only deliberate final
              work into the persistent workspace.
              """;

        var content = $"""
            > **This file is generated. Do not edit it directly.**
            > To change these instructions, edit the source files in `config/`.
            > Sections: identity.md, output_protocol.md, capabilities.md, memory.md

            {storageInstructions}

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

    /// <summary>Resolve only an explicitly configured entity-backed Workspace.</summary>
    public async Task<AgentWorkspace?> TryGetAsync(string? agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("An Agent must be selected");
        if (_cache.TryGetValue(agentId, out var cached))
            return cached;

        var agent = await agents.GetAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent not found: {agentId}");
        if (!Guid.TryParse(agent.Id, out var entityId))
            throw new InvalidOperationException($"Agent has an invalid entity id: {agent.Id}");
        var path = await workspacePaths.ResolveAsync(entityId, ct);
        if (path is null) return null;

        var workspace = new AgentWorkspace(path);
        workspace.EnsureDirectories();
        if (!workspace.UsesGeneratedHarnessProjection())
            workspace.MaterializeAgentFiles(agent);

        _cache[agentId] = workspace;
        return workspace;
    }

    /// <summary>
    /// Resolve the Agent's durable Workspace for a session, or materialize its entity-authored
    /// instructions into that session's disposable scratch allocation when none is configured.
    /// </summary>
    public async Task<AgentWorkspace> GetForSessionAsync(
        AgentInfo agent, AgentScratchAllocation scratch, CancellationToken ct = default)
        => await TryGetAsync(agent.Id, ct)
            ?? AgentWorkspace.CreateDisposable(scratch.Path, agent);

    /// <summary>Resolve a durable Workspace. Journal and memory operations deliberately stay strict.</summary>
    public async Task<AgentWorkspace> GetAsync(string? agentId, CancellationToken ct = default)
    {
        var workspace = await TryGetAsync(agentId, ct);
        if (workspace is not null) return workspace;

        var agent = await agents.GetAgentAsync(agentId!, ct);
        throw new InvalidOperationException(
            $"The entity-backed workspace for Agent '{agent?.Name ?? agentId}' is unavailable");
    }

    public void Invalidate(string? agentId = null)
    {
        if (agentId != null) _cache.TryRemove(agentId, out _);
        else _cache.Clear();
    }
}
