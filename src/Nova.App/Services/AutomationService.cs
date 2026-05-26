using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class AutomationService
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<Automation> _automations = [];
    private Task? _loop;
    private CancellationTokenSource? _cts;

    private static readonly string StorePath = "memory/meta/automations.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AutomationService(NovaEngine engine, MemoryManager memory, LogService log)
    {
        _engine = engine;
        _memory = memory;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Load();
        _loop = RunAsync(_cts.Token);
        _log.Info("automations", $"Started with {_automations.Count} automation(s)");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) await _loop;
    }

    public void Add(Automation automation)
    {
        automation.NextRun = CalculateNextRun(automation);
        _automations.Add(automation);
        Save();
        _log.Info("automations", $"Added: {automation.Name} [{automation.ActionType}] next={automation.NextRun:g}");
    }

    public bool Remove(string name)
    {
        var a = _automations.FirstOrDefault(x => x.Name == name);
        if (a == null) return false;
        _automations.Remove(a);
        Save();
        _log.Info("automations", $"Removed: {name}");
        return true;
    }

    public IReadOnlyList<Automation> GetAll() => _automations.AsReadOnly();

    public int ActiveCount => _automations.Count(a => a.Enabled);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                var now = DateTime.UtcNow;
                var due = _automations.Where(a => a.Enabled && a.NextRun <= now).ToList();

                foreach (var automation in due)
                {
                    try
                    {
                        var result = await ExecuteAsync(automation, ct);
                        automation.LastRun = now;
                        automation.LastResultJson = result != null
                            ? JsonSerializer.Serialize(result, JsonOptions)
                            : null;

                        if (result?.Triggered == true && automation.ReportToDiscussionId != null)
                        {
                            await ReportToDiscussionAsync(automation, result, ct);
                        }

                        if (result?.Triggered == true && automation.RemoveOnTrigger)
                        {
                            _automations.Remove(automation);
                            _log.Info("automations", $"Auto-removed after trigger: {automation.Name}");
                        }
                        else
                        {
                            automation.NextRun = CalculateNextRun(automation);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error("automations", $"[{automation.Name}] failed: {ex.Message}");
                        automation.NextRun = CalculateNextRun(automation);
                    }
                    finally
                    {
                        Save();
                    }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<AutomationResult?> ExecuteAsync(Automation automation, CancellationToken ct)
    {
        return automation.ActionType switch
        {
            "ai-session" => await ExecuteAiSessionAsync(automation, ct),
            "http-check" => await ExecuteHttpCheckAsync(automation, ct),
            "builtin:backup" => await ExecuteBackupAsync(ct),
            _ => throw new InvalidOperationException($"Unknown action type: {automation.ActionType}"),
        };
    }

    private async Task<AutomationResult> ExecuteAiSessionAsync(Automation automation, CancellationToken ct)
    {
        var config = Deserialize<AiSessionConfig>(automation.ActionConfigJson);
        _log.Info("automations", $"[{automation.Name}] Invoking AI session");

        var response = await _engine.InvokeForAutomationAsync(
            automation.Name, config.Prompt, config.SystemPromptHint, ct);

        return new AutomationResult
        {
            Triggered = true,
            Summary = response.Text ?? "(no response)",
            SessionId = response.SessionId,
        };
    }

    private async Task<AutomationResult> ExecuteHttpCheckAsync(Automation automation, CancellationToken ct)
    {
        var config = Deserialize<HttpCheckConfig>(automation.ActionConfigJson);
        _log.Info("automations", $"[{automation.Name}] Checking {config.Url}");

        var method = (config.Method?.ToUpperInvariant()) switch
        {
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            _ => HttpMethod.Get,
        };

        var request = new HttpRequestMessage(method, config.Url);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (config.Condition == null)
        {
            return new AutomationResult
            {
                Triggered = response.IsSuccessStatusCode,
                Summary = $"HTTP {(int)response.StatusCode}",
                Data = body,
            };
        }

        bool matched = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = ResolveJsonPath(doc.RootElement, config.Condition.Field);
            if (element != null)
            {
                var actual = element.Value.ValueKind == JsonValueKind.String
                    ? element.Value.GetString()
                    : element.Value.GetRawText();
                matched = string.Equals(actual, config.Condition.Equals, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            _log.Warn("automations", $"[{automation.Name}] Could not parse response as JSON");
        }

        return new AutomationResult
        {
            Triggered = matched,
            Summary = matched
                ? $"Condition met: {config.Condition.Field} == {config.Condition.Equals}"
                : $"Waiting: {config.Condition.Field} != {config.Condition.Equals}",
            Data = body,
        };
    }

    private static JsonElement? ResolveJsonPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (!current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }
        return current;
    }

    private async Task<AutomationResult> ExecuteBackupAsync(CancellationToken ct)
    {
        await _memory.BackupAsync();
        _log.Info("automations", "Daily backup completed");
        return new AutomationResult { Triggered = true, Summary = "Backup completed" };
    }

    private async Task ReportToDiscussionAsync(Automation automation, AutomationResult result, CancellationToken ct)
    {
        var discussionId = automation.ReportToDiscussionId!;
        _log.Info("automations", $"[{automation.Name}] Reporting to discussion {discussionId}");

        try
        {
            var eventContent = $"""
                <nova-event source="automation:{automation.Name}" type="{automation.ActionType}">
                {result.Summary}
                </nova-event>
                """;

            await _http.PostAsJsonAsync(
                $"http://localhost:18803/api/discussions/{discussionId}/event",
                new { content = eventContent, source = automation.Name }, ct);
        }
        catch (Exception ex)
        {
            _log.Error("automations", $"[{automation.Name}] Failed to report: {ex.Message}");
        }
    }

    private async Task<string?> GetSessionIdForDiscussion(string discussionId, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(
                $"http://localhost:18803/api/discussions/{discussionId}", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("discussion", out var disc)
                && disc.TryGetProperty("sessionId", out var sid))
                return sid.GetString();
        }
        catch { }
        return null;
    }

    private static DateTime CalculateNextRun(Automation automation)
    {
        if (!automation.Enabled) return DateTime.MaxValue;

        try
        {
            var expr = CronExpression.Parse(automation.Schedule, CronFormat.IncludeSeconds);
            var next = expr.GetNextOccurrence(DateTime.UtcNow);
            if (next.HasValue) return next.Value;
        }
        catch
        {
            try
            {
                var expr = CronExpression.Parse(automation.Schedule);
                var next = expr.GetNextOccurrence(DateTime.UtcNow);
                if (next.HasValue) return next.Value;
            }
            catch { }
        }

        return DateTime.UtcNow.AddMinutes(1);
    }

    private void Load()
    {
        var json = _memory.ReadMemoryFile(StorePath);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<Automation>>(json, JsonOptions);
                if (items != null)
                {
                    _automations.AddRange(items);
                    foreach (var a in _automations.Where(a => a.Enabled))
                        a.NextRun = CalculateNextRun(a);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("automations", $"Failed to load: {ex.Message}");
            }
        }

        EnsureBuiltIns();
    }

    private void EnsureBuiltIns()
    {
        var changed = false;

        if (_automations.All(a => a.Name != "system:backup"))
        {
            _automations.Add(new Automation
            {
                Name = "system:backup",
                Description = "Daily memory backup",
                Schedule = "0 3 * * *",
                ActionType = "builtin:backup",
                Enabled = true,
            });
            _log.Info("automations", "Added built-in backup automation");
            changed = true;
        }

        if (_automations.All(a => a.Name != "system:dreaming"))
        {
            var dreamingSkill = _memory.ReadMemoryFile("config/skills/dreaming.md") ?? "";
            _automations.Add(new Automation
            {
                Name = "system:dreaming",
                Description = "Nightly memory consolidation",
                Schedule = "0 4 * * *",
                ActionType = "ai-session",
                ActionConfigJson = JsonSerializer.Serialize(new AiSessionConfig
                {
                    Prompt = dreamingSkill,
                    SystemPromptHint = "dreaming",
                }, JsonOptions),
                Enabled = true,
            });
            _log.Info("automations", "Added built-in dreaming automation");
            changed = true;
        }

        if (changed) Save();
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_automations, JsonOptions);
        _memory.WriteMemoryFile(StorePath, json);
    }

    private static T Deserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrEmpty(json)) return new T();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? new T();
    }
}

public class Automation
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Schedule { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool RemoveOnTrigger { get; set; }

    public string ActionType { get; set; } = "";
    public string? ActionConfigJson { get; set; }

    public string? ReportToDiscussionId { get; set; }

    public DateTime? LastRun { get; set; }
    public DateTime NextRun { get; set; }
    public string? LastResultJson { get; set; }
}

public class AutomationResult
{
    public bool Triggered { get; set; }
    public string Summary { get; set; } = "";
    public string? Data { get; set; }
    public string? SessionId { get; set; }
}

public class AiSessionConfig
{
    public string Prompt { get; set; } = "";
    public string? SystemPromptHint { get; set; }
}

public class HttpCheckConfig
{
    public string Url { get; set; } = "";
    public string? Method { get; set; }
    public HttpCheckCondition? Condition { get; set; }
}

public class HttpCheckCondition
{
    public string Field { get; set; } = "";
    public new string Equals { get; set; } = "";
}

