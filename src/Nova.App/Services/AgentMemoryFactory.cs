using System.Collections.Concurrent;
using System.IO;
using RedBamboo.AppHost.Logging;
using Nova.App.Configuration;

namespace Nova.App.Services;

public class AgentMemoryFactory
{
    private readonly NovaConfig _config;
    private readonly MemoryManager _novaMemory;
    private readonly AgentResolver _resolver;
    private readonly LogService _log;
    private readonly ConcurrentDictionary<string, MemoryManager> _cache = new();

    public AgentMemoryFactory(NovaConfig config, MemoryManager novaMemory, AgentResolver resolver, LogService log)
    {
        _config = config;
        _novaMemory = novaMemory;
        _resolver = resolver;
        _log = log;
    }

    public async Task<MemoryManager> GetMemoryAsync(string? agentId)
    {
        if (agentId == null || agentId == _config.AgentId)
            return _novaMemory;

        if (_cache.TryGetValue(agentId, out var cached))
            return cached;

        var agent = await _resolver.GetAgentAsync(agentId);
        if (agent == null)
        {
            _log.Warn("agent-memory", $"Agent {agentId} not found, falling back to Nova");
            return _novaMemory;
        }

        var memory = new MemoryManager(agent.WorkspacePath, _log);
        memory.EnsureDirectories();

        MaterializeAgentFiles(agent, memory);

        _cache[agentId] = memory;
        _log.Info("agent-memory", $"Created memory manager for agent '{agent.Name}' at {agent.WorkspacePath}");
        return memory;
    }

    private static void MaterializeAgentFiles(AgentInfo agent, MemoryManager memory)
    {
        if (!string.IsNullOrEmpty(agent.Identity))
        {
            var path = Path.Combine(memory.RuntimeConfigPath, "identity.md");
            File.WriteAllText(path, agent.Identity);
        }

        if (!string.IsNullOrEmpty(agent.OutputProtocol))
        {
            var path = Path.Combine(memory.ConfigPath, "output_protocol.md");
            File.WriteAllText(path, agent.OutputProtocol);
        }

        if (!string.IsNullOrEmpty(agent.Capabilities))
        {
            var path = Path.Combine(memory.ConfigPath, "capabilities.md");
            File.WriteAllText(path, agent.Capabilities);
        }

        if (!string.IsNullOrEmpty(agent.MemoryInstructions))
        {
            var path = Path.Combine(memory.RuntimeConfigPath, "memory.md");
            File.WriteAllText(path, agent.MemoryInstructions);
        }
    }

    public async Task<string?> GetAgentNameAsync(string agentId)
    {
        var agent = await _resolver.GetAgentAsync(agentId);
        return agent?.Name;
    }

    public void InvalidateCache(string? agentId = null)
    {
        if (agentId != null)
            _cache.TryRemove(agentId, out _);
        else
            _cache.Clear();
    }
}
