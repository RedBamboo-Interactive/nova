using Microsoft.Extensions.DependencyInjection;
using Nova.App.Configuration;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class NovaEngine : IAsyncDisposable
{
    private readonly NovaConfig _config;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly RedComputeClient _redCompute;

    private AutomationService? _automations;
    private IServiceScopeFactory? _scopeFactory;
    private QualityModeService? _qualityModes;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public int ActiveAutomationCount => _automations?.ActiveCount ?? 0;
    public RedComputeClient RedCompute => _redCompute;
    public MemoryManager Memory => _memory;
    public AutomationService? Automations => _automations;
    public QualityModeService? QualityModes => _qualityModes;

    public NovaEngine(NovaConfig config, MemoryManager memory, LogService log)
    {
        _config = config;
        _memory = memory;
        _log = log;
        _redCompute = new RedComputeClient(config, log);
    }

    public void SetServiceScopeFactory(IServiceScopeFactory factory) => _scopeFactory = factory;
    public void SetQualityModes(QualityModeService qualityModes) => _qualityModes = qualityModes;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _automations = new AutomationService(this, _memory, _log, _scopeFactory, _qualityModes);
        await _automations.StartAsync(_cts.Token);

        _log.Info("engine", "Nova engine started");
    }

    public async Task StopAsync()
    {
        _log.Info("engine", "Nova engine stopping");
        _cts?.Cancel();

        if (_automations != null) await _automations.StopAsync();

        _redCompute.Dispose();
    }

    public async Task<ClaudeResponse> InvokeForAutomationAsync(string name, string prompt, string? hint, string? userId, CancellationToken ct, int? timeout = null, ResolvedMode? mode = null)
    {
        _log.Info("engine", $"Automation [{name}]: invoking");

        var request = new ClaudeRequest
        {
            Prompt = prompt,
            SystemPrompt = BuildAutomationPrompt(name),
            SystemPromptHint = hint ?? name,
            WorkingDirectory = _memory.WorkspacePath,
            AllowedTools = ["Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch", "TodoWrite"],
            MaxTurns = 50,
            Timeout = timeout,
        };

        return await _redCompute.InvokeAsync(request, userId, ct, mode);
    }

    private string BuildAutomationPrompt(string name)
    {
        var identity = _memory.ReadIdentity();
        var now = DateTimeOffset.Now;
        var date = now.ToString("yyyy-MM-dd");
        var day = now.ToString("dddd");
        var time = now.ToString("HH:mm");

        return $"""
            {identity}

            ---

            # Automation: {name}
            Current date: {date} ({day}), {time} local time.
            You are running an automated task. Be concise.
            If there is nothing to do, say so briefly.
            If you need to notify the user, write to memory/meta/notifications.md.
            """;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
