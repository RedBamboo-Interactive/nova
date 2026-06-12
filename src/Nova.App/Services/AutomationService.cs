using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Nova.App.Data;
using Nova.App.Data.Entities;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class AutomationService
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly IServiceScopeFactory? _scopeFactory;
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

    public AutomationService(NovaEngine engine, MemoryManager memory, LogService log, IServiceScopeFactory? scopeFactory = null)
    {
        _engine = engine;
        _memory = memory;
        _log = log;
        _scopeFactory = scopeFactory;
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
        if (!ValidateCron(automation.Schedule))
        {
            _log.Error("automations", $"Invalid cron expression for {automation.Name}: {automation.Schedule}");
            throw new ArgumentException($"Invalid cron expression: {automation.Schedule}");
        }

        automation.NextRun = CalculateNextRun(automation);
        _automations.Add(automation);
        Save();
        _log.Info("automations", $"Added: {automation.Name} [{automation.ActionType}] next={automation.NextRun:g}");
    }

    private static bool ValidateCron(string schedule)
    {
        try { CronExpression.Parse(schedule, CronFormat.IncludeSeconds); return true; }
        catch
        {
            try { CronExpression.Parse(schedule); return true; }
            catch { return false; }
        }
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

    /// <summary>
    /// Apply a partial update — only non-null fields change. Returns the updated automation,
    /// or null when not found. Throws ArgumentException on an invalid cron expression.
    /// </summary>
    public Automation? Update(string name, AutomationUpdate update)
    {
        var a = _automations.FirstOrDefault(x => x.Name == name);
        if (a == null) return null;

        if (update.Schedule != null)
        {
            if (!ValidateCron(update.Schedule))
                throw new ArgumentException($"Invalid cron expression: {update.Schedule}");
            a.Schedule = update.Schedule;
        }

        if (update.Description != null) a.Description = update.Description;
        if (update.Enabled.HasValue) a.Enabled = update.Enabled.Value;
        if (update.Icon != null) a.Icon = update.Icon;
        if (update.ActionConfigJson != null) a.ActionConfigJson = update.ActionConfigJson;
        if (update.ReportToDiscussionId != null) a.ReportToDiscussionId = update.ReportToDiscussionId;
        if (update.RemoveOnTrigger.HasValue) a.RemoveOnTrigger = update.RemoveOnTrigger.Value;
        if (update.ExpiresAt.HasValue) a.ExpiresAt = update.ExpiresAt.Value;
        if (update.MaxFailures.HasValue) a.MaxFailures = update.MaxFailures.Value;

        a.NextRun = CalculateNextRun(a);
        Save();
        _log.Info("automations", $"Updated: {name} next={a.NextRun:g}");
        return a;
    }

    public async Task<AutomationResult?> TriggerAsync(string name, CancellationToken ct)
    {
        var automation = _automations.FirstOrDefault(x => x.Name == name);
        if (automation == null) return null;

        _log.Info("automations", $"[{automation.Name}] Manual trigger");
        try
        {
            var result = await ExecuteAsync(automation, ct);
            automation.LastRun = DateTime.UtcNow;
            automation.LastError = null;
            automation.ConsecutiveFailures = 0;
            automation.LastResultJson = result != null
                ? JsonSerializer.Serialize(result, JsonOptions)
                : null;
            automation.NextRun = CalculateNextRun(automation);
            Save();
            NovaMirror.PublishAutomationRun(automation.Name, result?.Triggered == true, result?.Summary, null);

            if (result?.Triggered == true && automation.ReportToDiscussionId != null)
                await ReportToDiscussionAsync(automation, result, ct);

            return result;
        }
        catch (Exception ex)
        {
            automation.ConsecutiveFailures++;
            automation.LastError = ex.Message;
            automation.LastRun = DateTime.UtcNow;
            automation.LastResultJson = JsonSerializer.Serialize(
                new AutomationResult { Triggered = false, Summary = $"Failed: {ex.Message}" }, JsonOptions);
            automation.NextRun = CalculateNextRun(automation);
            Save();
            NovaMirror.PublishAutomationRun(automation.Name, false, null, ex.Message);
            _log.Error("automations", $"[{automation.Name}] Manual trigger failed ({automation.ConsecutiveFailures}x): {ex.Message}");
            return new AutomationResult { Triggered = false, Summary = $"Failed: {ex.Message}" };
        }
    }

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
                    if (automation.ExpiresAt.HasValue && now >= automation.ExpiresAt.Value)
                    {
                        automation.Enabled = false;
                        automation.LastError = "Expired";
                        _log.Warn("automations", $"[{automation.Name}] Expired, disabling");
                        if (automation.ReportToDiscussionId != null)
                            await ReportFailureAsync(automation, "Watcher expired without triggering", ct);
                        if (automation.RemoveOnTrigger) _automations.Remove(automation);
                        Save();
                        continue;
                    }

                    try
                    {
                        var result = await ExecuteAsync(automation, ct);
                        automation.LastRun = now;
                        automation.LastError = null;
                        automation.ConsecutiveFailures = 0;
                        automation.LastResultJson = result != null
                            ? JsonSerializer.Serialize(result, JsonOptions)
                            : null;
                        NovaMirror.PublishAutomationRun(automation.Name, result?.Triggered == true, result?.Summary, null);

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
                        automation.ConsecutiveFailures++;
                        automation.LastError = ex.Message;
                        NovaMirror.PublishAutomationRun(automation.Name, false, null, ex.Message);
                        _log.Error("automations", $"[{automation.Name}] failed ({automation.ConsecutiveFailures}x): {ex.Message}");

                        var max = automation.MaxFailures > 0 ? automation.MaxFailures : 20;
                        if (automation.ConsecutiveFailures >= max)
                        {
                            automation.Enabled = false;
                            _log.Error("automations", $"[{automation.Name}] Hit {max} consecutive failures, disabling");
                            if (automation.ReportToDiscussionId != null)
                                await ReportFailureAsync(automation, $"Failed {max} times consecutively: {ex.Message}", ct);
                            if (automation.RemoveOnTrigger) _automations.Remove(automation);
                        }
                        else
                        {
                            automation.NextRun = CalculateNextRun(automation);
                        }
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

        var prompt = config.Prompt;

        if (config.PreCreateDiscussion)
        {
            var discussionId = await PreCreateDiscussionAsync(automation.OwnerId, ct);
            if (discussionId != null)
            {
                automation.ReportToDiscussionId = discussionId;
                prompt = $"<nova-context pre-created-discussion=\"{discussionId}\">\n"
                    + $"A discussion has already been created for you with ID: {discussionId}.\n"
                    + $"Post your content to it using: POST http://localhost:18803/api/discussions/{discussionId}/nova-message\n"
                    + "Do NOT create a new discussion — use the one provided.\n"
                    + "</nova-context>\n\n" + prompt;
            }
        }

        var response = await _engine.InvokeForAutomationAsync(
            automation.Name, prompt, config.SystemPromptHint, automation.OwnerId, ct, config.Timeout, config.QualityMode);

        return new AutomationResult
        {
            Triggered = true,
            Summary = response.Text ?? "(no response)",
            SessionId = response.SessionId,
        };
    }

    private async Task<string?> PreCreateDiscussionAsync(string? ownerId, CancellationToken ct)
    {
        if (_scopeFactory == null)
        {
            _log.Error("automations", "Cannot pre-create discussion: no service scope factory");
            return null;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaDbContext>();

            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
                OwnerId = ownerId,
            };

            db.Discussions.Add(discussion);
            await db.SaveChangesAsync(ct);

            _log.Info("automations", $"Pre-created discussion {discussion.Id} (owner: {ownerId ?? "none"})");
            return discussion.Id;
        }
        catch (Exception ex)
        {
            _log.Error("automations", $"Failed to pre-create discussion: {ex.Message}");
        }
        return null;
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

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");

        if (config.Condition == null)
        {
            return new AutomationResult
            {
                Triggered = true,
                Summary = $"HTTP {(int)response.StatusCode}",
                Data = body,
            };
        }

        using var doc = JsonDocument.Parse(body);
        var element = ResolveJsonPath(doc.RootElement, config.Condition.Field);
        if (element == null)
            throw new InvalidOperationException($"Field '{config.Condition.Field}' not found in response");

        var actual = element.Value.ValueKind == JsonValueKind.String
            ? element.Value.GetString()
            : element.Value.GetRawText();
        var matched = string.Equals(actual, config.Condition.Equals, StringComparison.OrdinalIgnoreCase);

        return new AutomationResult
        {
            Triggered = matched,
            Summary = matched
                ? $"Condition met: {config.Condition.Field} == {config.Condition.Equals}"
                : $"Waiting: {config.Condition.Field} = {actual}",
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

    private async Task ReportFailureAsync(Automation automation, string reason, CancellationToken ct)
    {
        var discussionId = automation.ReportToDiscussionId!;
        _log.Warn("automations", $"[{automation.Name}] Reporting failure to discussion {discussionId}");

        try
        {
            var eventContent = $"""
                <nova-event source="automation:{automation.Name}" type="watcher-failed">
                Watcher "{automation.Name}" failed: {reason}
                </nova-event>
                """;

            await _http.PostAsJsonAsync(
                $"http://localhost:18803/api/discussions/{discussionId}/event",
                new { content = eventContent, source = automation.Name }, ct);
        }
        catch (Exception ex)
        {
            _log.Error("automations", $"[{automation.Name}] Failed to report failure: {ex.Message}");
        }
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
            var expr = CronExpression.Parse(automation.Schedule);
            var next = expr.GetNextOccurrence(DateTime.UtcNow);
            if (next.HasValue) return next.Value;
        }

        return DateTime.MaxValue;
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

    private string? ResolveDefaultOwner()
    {
        if (_scopeFactory == null) return null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NovaDbContext>();
            return db.Discussions
                .Where(d => d.OwnerId != null && d.OwnerId != "local-user")
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => d.OwnerId)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private void EnsureBuiltIns()
    {
        var changed = false;
        var defaultOwner = ResolveDefaultOwner();

        if (_automations.All(a => a.Name != "system:backup"))
        {
            _automations.Add(new Automation
            {
                Name = "system:backup",
                Description = "Daily memory backup",
                Schedule = "0 3 * * *",
                ActionType = "builtin:backup",
                OwnerId = defaultOwner,
                Enabled = true,
            });
            _log.Info("automations", "Added built-in backup automation");
            changed = true;
        }

        var dreamingSkill = _memory.ReadMemoryFile("config/skills/dreaming.md") ?? "";
        var existingDreaming = _automations.FirstOrDefault(a => a.Name == "system:dreaming");
        if (existingDreaming == null)
        {
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
                    Timeout = 3600,
                    QualityMode = "standard",
                }, JsonOptions),
                OwnerId = defaultOwner,
                Enabled = true,
            });
            _log.Info("automations", "Added built-in dreaming automation");
            changed = true;
        }
        else
        {
            var currentConfig = Deserialize<AiSessionConfig>(existingDreaming.ActionConfigJson);
            var dreamingChanged = false;
            if (currentConfig.Prompt != dreamingSkill)
            {
                currentConfig.Prompt = dreamingSkill;
                dreamingChanged = true;
                _log.Info("automations", "Synced dreaming prompt from skill file");
            }
            if (currentConfig.Timeout != 3600)
            {
                currentConfig.Timeout = 3600;
                dreamingChanged = true;
                _log.Info("automations", "Synced dreaming timeout to 3600s");
            }
            if (currentConfig.QualityMode != "standard")
            {
                currentConfig.QualityMode = "standard";
                dreamingChanged = true;
                _log.Info("automations", "Synced dreaming quality mode to standard");
            }
            if (dreamingChanged)
            {
                existingDreaming.ActionConfigJson = JsonSerializer.Serialize(currentConfig, JsonOptions);
                changed = true;
            }
        }

        // Backfill OwnerId on any automations that lack one
        if (defaultOwner != null)
        {
            foreach (var a in _automations.Where(a => a.OwnerId == null))
            {
                a.OwnerId = defaultOwner;
                _log.Info("automations", $"Set default owner on {a.Name}");
                changed = true;
            }
        }

        // Ensure morning-greeting uses PreCreateDiscussion and the fast quality mode
        var greeting = _automations.FirstOrDefault(a => a.Name == "morning-greeting");
        if (greeting != null)
        {
            var greetingConfig = Deserialize<AiSessionConfig>(greeting.ActionConfigJson);
            var greetingChanged = false;
            if (!greetingConfig.PreCreateDiscussion)
            {
                greetingConfig.PreCreateDiscussion = true;
                greetingConfig.Prompt = SyncMorningGreetingPrompt(greetingConfig.Prompt);
                _log.Info("automations", "Enabled PreCreateDiscussion on morning-greeting");
                greetingChanged = true;
            }
            if (greetingConfig.QualityMode != "fast")
            {
                greetingConfig.QualityMode = "fast";
                _log.Info("automations", "Set morning-greeting quality mode to fast");
                greetingChanged = true;
            }
            if (greetingChanged)
            {
                greeting.ActionConfigJson = JsonSerializer.Serialize(greetingConfig, JsonOptions);
                changed = true;
            }
        }

        // Ensure weekly-checkin uses the fast quality mode
        var weekly = _automations.FirstOrDefault(a => a.Name == "weekly-checkin");
        if (weekly != null && weekly.ActionType == "ai-session")
        {
            var weeklyConfig = Deserialize<AiSessionConfig>(weekly.ActionConfigJson);
            if (weeklyConfig.QualityMode != "fast")
            {
                weeklyConfig.QualityMode = "fast";
                weekly.ActionConfigJson = JsonSerializer.Serialize(weeklyConfig, JsonOptions);
                _log.Info("automations", "Set weekly-checkin quality mode to fast");
                changed = true;
            }
        }

        if (changed) Save();
    }

    private static string SyncMorningGreetingPrompt(string prompt)
    {
        const string oldStep3 = "## Step 3: Send it\n\nCreate the discussion:";
        if (!prompt.Contains(oldStep3)) return prompt;

        var idx = prompt.IndexOf(oldStep3, StringComparison.Ordinal);
        var before = prompt[..idx];

        return before + """
            ## Step 3: Send it

            A discussion has already been pre-created for you. Its ID is in the `<nova-context pre-created-discussion="...">` tag at the top of this prompt.

            Inject your greeting using the pre-created discussion ID:
            ```bash
            curl -s -X POST http://localhost:18803/api/discussions/{id}/nova-message -H "Content-Type: application/json" -d '{"content": "your greeting here", "title": "A casual title for the discussion"}'
            ```

            The title should be creative and different each day. Something that hints at the vibe, not a label.
            """;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_automations, JsonOptions);
        _memory.WriteMemoryFile(StorePath, json);
        NovaMirror.PublishAutomations(_automations);
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

    public string? Icon { get; set; }

    public string ActionType { get; set; } = "";
    public string? ActionConfigJson { get; set; }

    public string? ReportToDiscussionId { get; set; }

    public DateTime? LastRun { get; set; }
    public DateTime NextRun { get; set; }
    public string? LastResultJson { get; set; }

    public int ConsecutiveFailures { get; set; }
    public int MaxFailures { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? LastError { get; set; }
    public string? OwnerId { get; set; }
}

/// <summary>Partial update for an automation — null fields are left untouched.</summary>
public class AutomationUpdate
{
    public string? Description { get; set; }
    public string? Schedule { get; set; }
    public bool? Enabled { get; set; }
    public string? Icon { get; set; }
    public string? ActionConfigJson { get; set; }
    public string? ReportToDiscussionId { get; set; }
    public bool? RemoveOnTrigger { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? MaxFailures { get; set; }
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
    public bool PreCreateDiscussion { get; set; }
    public int? Timeout { get; set; }

    /// <summary>Abstract quality tier (fast, standard, deep, research) resolved at run time. Null = backend default.</summary>
    public string? QualityMode { get; set; }
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

