using Nova.App.Configuration;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaEngine : IAsyncDisposable
{
    private readonly NovaConfig _config;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly RedComputeClient _redCompute;

    private HeartbeatService? _heartbeat;
    private SchedulerService? _scheduler;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public int ActiveHeartbeatCount => _heartbeat?.ActiveCount ?? 0;
    public RedComputeClient RedCompute => _redCompute;
    public MemoryManager Memory => _memory;
    public SchedulerService? Scheduler => _scheduler;
    public HeartbeatService? Heartbeat => _heartbeat;

    public NovaEngine(NovaConfig config, MemoryManager memory, LogService log)
    {
        _config = config;
        _memory = memory;
        _log = log;
        _redCompute = new RedComputeClient(config, log);
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

    public async Task<string?> InvokeForHeartbeatAsync(string purpose, string prompt, CancellationToken ct)
    {
        _log.Info("engine", $"Heartbeat [{purpose}]: invoking");

        var request = new ClaudeRequest
        {
            Prompt = prompt,
            SystemPrompt = BuildHeartbeatPrompt(purpose),
            SystemPromptHint = purpose,
            WorkingDirectory = _memory.WorkspacePath,
            AllowedTools = ["Read", "Write", "Edit", "Glob", "Grep", "Bash", "PowerShell", "WebFetch", "WebSearch", "TodoWrite"],
        };

        var response = await _redCompute.InvokeClaudeAsync(request, ct);
        return response.Text;
    }

    private string BuildHeartbeatPrompt(string purpose)
    {
        var identity = _memory.ReadIdentity();
        var heartbeats = _memory.ReadHeartbeats();

        return $"""
            {identity}

            ---

            # Heartbeat: {purpose}
            You are running a scheduled task. Unless the task instructions say otherwise, be concise.
            If there is nothing to do, say so briefly.
            If you need to notify the user, write to memory/meta/notifications.md.

            # Active heartbeats
            {heartbeats}
            """;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
