using System.Text.Json;
using Cronos;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class SchedulerService
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly List<ScheduledTask> _tasks = [];
    private Task? _loop;
    private CancellationTokenSource? _cts;

    private static readonly string SchedulePath = "memory/meta/schedules.json";

    public SchedulerService(NovaEngine engine, MemoryManager memory, LogService log)
    {
        _engine = engine;
        _memory = memory;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LoadTasks();
        _loop = RunAsync(_cts.Token);
        _log.Info("scheduler", $"Started with {_tasks.Count} task(s)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) await _loop;
    }

    public void AddTask(ScheduledTask task)
    {
        _tasks.Add(task);
        SaveTasks();
        _log.Info("scheduler", $"Added task: {task.Name} (next: {task.NextRun:g})");
    }

    public bool RemoveTask(string name)
    {
        var task = _tasks.FirstOrDefault(t => t.Name == name);
        if (task == null) return false;
        _tasks.Remove(task);
        SaveTasks();
        _log.Info("scheduler", $"Removed task: {name}");
        return true;
    }

    public IReadOnlyList<ScheduledTask> GetAll() => _tasks.AsReadOnly();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                var now = DateTime.UtcNow;
                var due = _tasks.Where(t => t.NextRun <= now && t.Enabled).ToList();

                foreach (var task in due)
                {
                    try
                    {
                        await _engine.InvokeForHeartbeatAsync($"schedule:{task.Name}", task.Prompt, ct);
                        task.LastRun = now;
                    }
                    catch (Exception ex)
                    {
                        _log.Error("scheduler", $"Task [{task.Name}] failed: {ex.Message}");
                    }
                    finally
                    {
                        if (task.Recurring)
                            task.NextRun = CalculateNextRun(task);
                        else
                            task.Enabled = false;

                        SaveTasks();
                    }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private static DateTime CalculateNextRun(ScheduledTask task)
    {
        if (!task.Recurring)
            return DateTime.MaxValue;

        if (!string.IsNullOrEmpty(task.CronExpression))
        {
            try
            {
                var expr = CronExpression.Parse(task.CronExpression);
                var next = expr.GetNextOccurrence(DateTime.UtcNow);
                if (next.HasValue) return next.Value;
            }
            catch { }
        }

        if (task.IntervalMinutes > 0)
            return DateTime.UtcNow.AddMinutes(task.IntervalMinutes);

        return DateTime.UtcNow.AddHours(24);
    }

    private void LoadTasks()
    {
        var json = _memory.ReadMemoryFile(SchedulePath);
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var tasks = JsonSerializer.Deserialize<List<ScheduledTask>>(json);
            if (tasks != null) _tasks.AddRange(tasks);
        }
        catch (Exception ex)
        {
            _log.Warn("scheduler", $"Failed to load schedules: {ex.Message}");
        }
    }

    private void SaveTasks()
    {
        var json = JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true });
        _memory.WriteMemoryFile(SchedulePath, json);
    }
}

public class ScheduledTask
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public DateTime NextRun { get; set; }
    public DateTime? LastRun { get; set; }
    public bool Recurring { get; set; }
    public string? CronExpression { get; set; }
    public int IntervalMinutes { get; set; }
    public bool Enabled { get; set; } = true;
}
