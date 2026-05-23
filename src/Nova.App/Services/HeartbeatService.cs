using System.Text.Json;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class HeartbeatService
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly List<HeartbeatDefinition> _heartbeats = [];
    private readonly List<Task> _loops = [];
    private CancellationTokenSource? _cts;

    public int ActiveCount => _heartbeats.Count;

    public HeartbeatService(NovaEngine engine, MemoryManager memory, LogService log)
    {
        _engine = engine;
        _memory = memory;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LoadHeartbeats();

        foreach (var hb in _heartbeats)
        {
            _loops.Add(RunLoopAsync(hb, _cts.Token));
        }

        // Always run maintenance loop
        _loops.Add(RunMaintenanceLoopAsync(_cts.Token));

        _log.Info("heartbeat", $"Started with {_heartbeats.Count} heartbeat(s)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        await Task.WhenAll(_loops);
        _loops.Clear();
    }

    public void AddHeartbeat(HeartbeatDefinition definition)
    {
        _heartbeats.Add(definition);
        SaveHeartbeats();

        if (_cts is { IsCancellationRequested: false })
        {
            _loops.Add(RunLoopAsync(definition, _cts.Token));
        }

        _log.Info("heartbeat", $"Added heartbeat: {definition.Name} (every {definition.IntervalMinutes}m)");
    }

    public bool RemoveHeartbeat(string name)
    {
        var hb = _heartbeats.FirstOrDefault(h => h.Name == name);
        if (hb == null) return false;

        hb.Cancelled = true;
        _heartbeats.Remove(hb);
        SaveHeartbeats();
        _log.Info("heartbeat", $"Removed heartbeat: {name}");
        return true;
    }

    public IReadOnlyList<HeartbeatDefinition> GetAll() => _heartbeats.AsReadOnly();

    private async Task RunLoopAsync(HeartbeatDefinition hb, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !hb.Cancelled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(hb.IntervalMinutes), ct);
                if (hb.Cancelled) break;

                await _engine.InvokeForHeartbeatAsync(hb.Name, hb.Prompt, ct);
                hb.LastRun = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error("heartbeat", $"Heartbeat [{hb.Name}] failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct);

                await _memory.BackupAsync();
                _log.Info("heartbeat", "Maintenance cycle completed");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error("heartbeat", $"Maintenance failed: {ex.Message}");
            }
        }
    }

    private void LoadHeartbeats()
    {
        var content = _memory.ReadHeartbeats();
        if (string.IsNullOrWhiteSpace(content)) return;

        // Heartbeats are stored as a simple markdown list with JSON metadata
        // Format: - **name** (every Xm): prompt <!-- {"intervalMinutes": X} -->
        // For now, we parse a JSON section at the bottom of the file
        var jsonMarker = content.IndexOf("```json", StringComparison.Ordinal);
        if (jsonMarker < 0) return;

        var jsonEnd = content.IndexOf("```", jsonMarker + 7, StringComparison.Ordinal);
        if (jsonEnd < 0) return;

        var json = content[(jsonMarker + 7)..jsonEnd].Trim();
        try
        {
            var defs = JsonSerializer.Deserialize<List<HeartbeatDefinition>>(json);
            if (defs != null) _heartbeats.AddRange(defs);
        }
        catch (Exception ex)
        {
            _log.Warn("heartbeat", $"Failed to parse heartbeats: {ex.Message}");
        }
    }

    private void SaveHeartbeats()
    {
        var json = JsonSerializer.Serialize(_heartbeats, new JsonSerializerOptions { WriteIndented = true });
        var content = $"""
            # Active Heartbeats

            {string.Join("\n", _heartbeats.Select(h => $"- **{h.Name}** (every {h.IntervalMinutes}m): {h.Description}"))}

            ```json
            {json}
            ```
            """;

        _memory.WriteHeartbeats(content);
    }
}

public class HeartbeatDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int IntervalMinutes { get; set; } = 60;
    public DateTime? LastRun { get; set; }
    public bool Cancelled { get; set; }
}
