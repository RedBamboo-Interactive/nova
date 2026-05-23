using System.Collections.Concurrent;
using Nova.App.Configuration;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaEngine : IAsyncDisposable
{
    private readonly NovaConfig _config;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly RedComputeClient _redCompute;
    private readonly SemaphoreSlim _claudeSemaphore;
    private readonly ConcurrentDictionary<string, ConversationContext> _contexts = new();

    private HeartbeatService? _heartbeat;
    private SchedulerService? _scheduler;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public int ActiveHeartbeatCount => _heartbeat?.ActiveCount ?? 0;

    public NovaEngine(NovaConfig config, MemoryManager memory, LogService log)
    {
        _config = config;
        _memory = memory;
        _log = log;
        _redCompute = new RedComputeClient(config, log);
        _claudeSemaphore = new SemaphoreSlim(config.MaxConcurrentInvocations);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _heartbeat = new HeartbeatService(this, _memory, _log);
        _scheduler = new SchedulerService(this, _memory, _log);

        await _heartbeat.StartAsync(_cts.Token);
        await _scheduler.StartAsync(_cts.Token);

        _log.Info("engine", "Nova engine started");
    }

    public async Task StopAsync()
    {
        _log.Info("engine", "Nova engine stopping");
        _cts?.Cancel();

        if (_heartbeat != null) await _heartbeat.StopAsync();
        if (_scheduler != null) await _scheduler.StopAsync();

        _redCompute.Dispose();
    }

    public async Task<ChatResponse> ChatAsync(string contextId, string message, CancellationToken ct = default)
    {
        var context = _contexts.GetOrAdd(contextId, id => new ConversationContext(id));

        await _claudeSemaphore.WaitAsync(ct);
        try
        {
            context.Status = ConversationStatus.Thinking;
            _log.Info("engine", $"Chat [{contextId}]: processing");

            var systemPrompt = BuildSystemPrompt(context);
            var memoryManifest = _memory.GetMemoryManifest();

            var request = new ClaudeRequest
            {
                Prompt = message,
                SystemPrompt = systemPrompt,
                SystemPromptHint = "chat",
                WorkingDirectory = _memory.WorkspacePath,
                AllowedTools = ["Read", "Write", "Edit", "Glob", "Grep", "WebFetch"],
                Context = new Dictionary<string, string>
                {
                    ["memory_manifest"] = string.Join("\n", memoryManifest),
                    ["conversation_id"] = contextId,
                }
            };

            var response = await _redCompute.InvokeClaudeAsync(request, ct);

            context.Status = ConversationStatus.Idle;
            context.LastActivity = DateTime.UtcNow;

            return new ChatResponse
            {
                Text = response.Text ?? "",
                ToolCalls = response.ToolCalls,
                ContextId = contextId,
            };
        }
        finally
        {
            _claudeSemaphore.Release();
        }
    }

    public async Task<string?> InvokeForHeartbeatAsync(string purpose, string prompt, CancellationToken ct)
    {
        if (!await _claudeSemaphore.WaitAsync(TimeSpan.Zero, ct))
        {
            _log.Debug("engine", $"Heartbeat [{purpose}]: skipped, Claude is busy");
            return null;
        }

        try
        {
            _log.Info("engine", $"Heartbeat [{purpose}]: invoking");

            var request = new ClaudeRequest
            {
                Prompt = prompt,
                SystemPrompt = BuildHeartbeatPrompt(purpose),
                SystemPromptHint = purpose,
                WorkingDirectory = _memory.WorkspacePath,
                AllowedTools = ["Read", "Write", "Edit", "Glob", "Grep", "WebFetch"],
            };

            var response = await _redCompute.InvokeClaudeAsync(request, ct);
            return response.Text;
        }
        finally
        {
            _claudeSemaphore.Release();
        }
    }

    public ConversationContext? GetContext(string contextId)
    {
        return _contexts.TryGetValue(contextId, out var ctx) ? ctx : null;
    }

    public IReadOnlyCollection<ConversationContext> GetAllContexts()
    {
        return _contexts.Values.ToList().AsReadOnly();
    }

    private string BuildSystemPrompt(ConversationContext context)
    {
        var identity = _memory.ReadIdentity();
        var protocol = _memory.ReadOutputProtocol();
        var capabilities = _memory.ReadCapabilities();

        return $"""
            {identity}

            ---

            # Output Protocol
            {protocol}

            ---

            # Capabilities
            {capabilities}

            ---

            # Memory
            You have access to a file-based memory system in your working directory.
            Use Read/Write/Edit tools to interact with memory files.
            The memory manifest lists all available files — read what's relevant, don't load everything.

            # Conversation context: {context.Id}
            """;
    }

    private string BuildHeartbeatPrompt(string purpose)
    {
        var identity = _memory.ReadIdentity();
        var heartbeats = _memory.ReadHeartbeats();

        return $"""
            {identity}

            ---

            # Heartbeat: {purpose}
            You are running a scheduled heartbeat task. Be concise. If there is nothing to do, say so briefly.
            If you need to notify the user, write to memory/meta/notifications.md.

            # Active heartbeats
            {heartbeats}
            """;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _claudeSemaphore.Dispose();
    }
}

public class ChatResponse
{
    public string Text { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
    public string ContextId { get; set; } = "";
}

public class ConversationContext
{
    public string Id { get; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Idle;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public ConversationContext(string id) => Id = id;
}

public enum ConversationStatus
{
    Idle,
    Thinking,
    ToolUse,
}
