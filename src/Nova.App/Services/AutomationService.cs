using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Nova.App.Configuration;
using Nova.App.Data;
using Nova.App.Data.Entities;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class AutomationService
{
    private readonly NovaEngine _engine;
    private readonly MemoryManager _memory;
    private readonly LogService _log;
    private readonly NovaConfig _config;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly List<Automation> _automations = [];
    private readonly Dictionary<string, Guid> _entityIds = [];
    private Task? _loop;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions HttpJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AutomationService(NovaEngine engine, MemoryManager memory, LogService log, NovaConfig config, IServiceScopeFactory? scopeFactory = null)
    {
        _engine = engine;
        _memory = memory;
        _log = log;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await LoadAsync();
        _loop = RunAsync(_cts.Token);
        _log.Info("automations", $"Started with {_automations.Count} automation(s)");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) await _loop;
    }

    public async Task AddAsync(Automation automation)
    {
        if (!ValidateCron(automation.Schedule))
        {
            _log.Error("automations", $"Invalid cron expression for {automation.Name}: {automation.Schedule}");
            throw new ArgumentException($"Invalid cron expression: {automation.Schedule}");
        }

        automation.NextRun = CalculateNextRun(automation);
        _automations.Add(automation);

        var entityId = await UpsertToRedLeafAsync(automation);
        if (entityId.HasValue)
            _entityIds[automation.Name] = entityId.Value;

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

    public async Task<bool> RemoveAsync(string name)
    {
        var a = _automations.FirstOrDefault(x => x.Name == name);
        if (a == null) return false;
        _automations.Remove(a);
        await DeleteFromRedLeafAsync(name);
        _log.Info("automations", $"Removed: {name}");
        return true;
    }

    public IReadOnlyList<Automation> GetAll() => _automations.AsReadOnly();

    public int ActiveCount => _automations.Count(a => a.Enabled);

    /// <summary>
    /// Apply a partial update — only non-null fields change. Returns the updated automation,
    /// or null when not found. Throws ArgumentException on an invalid cron expression.
    /// </summary>
    public async Task<Automation?> UpdateAsync(string name, AutomationUpdate update)
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
        await PatchRedLeafDataAsync(name, a);
        _log.Info("automations", $"Updated: {name} next={a.NextRun:g}");
        return a;
    }

    /// <summary>Refreshes or adds an automation from RedLeaf after a WS entity.updated/created event.</summary>
    public async Task RefreshAutomationAsync(Guid entityId)
    {
        try
        {
            using var http = BuildRedLeafClient();
            var resp = await http.GetAsync($"api/entities/{entityId}");
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (doc.RootElement.TryGetProperty("typeSlug", out var typeEl)
                && typeEl.GetString() != "automation") return;

            var updated = MapEntity(doc.RootElement);
            if (updated == null) return;

            var name = _entityIds.FirstOrDefault(kv => kv.Value == entityId).Key;
            var existing = name != null
                ? _automations.FirstOrDefault(a => a.Name == name)
                : _automations.FirstOrDefault(a => a.Name == updated.Name);

            if (existing != null)
            {
                existing.Description = updated.Description;
                existing.Schedule = updated.Schedule;
                existing.Enabled = updated.Enabled;
                existing.RemoveOnTrigger = updated.RemoveOnTrigger;
                existing.Icon = updated.Icon;
                existing.ActionType = updated.ActionType;
                existing.ActionConfigJson = updated.ActionConfigJson;
                existing.ReportToDiscussionId = updated.ReportToDiscussionId;
                existing.MaxFailures = updated.MaxFailures;
                existing.ExpiresAt = updated.ExpiresAt;
                existing.OwnerId = updated.OwnerId;

                if (existing.Enabled)
                    existing.NextRun = CalculateNextRun(existing);

                _entityIds[existing.Name] = entityId;
                _log.Info("automations", $"Refreshed {existing.Name} from RedLeaf WS event");
            }
            else
            {
                updated.NextRun = CalculateNextRun(updated);
                _automations.Add(updated);
                _entityIds[updated.Name] = entityId;
                _log.Info("automations", $"Added new automation {updated.Name} from RedLeaf WS event");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("automations", $"RefreshAutomation failed for {entityId}: {ex.Message}");
        }
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
                        if (automation.RemoveOnTrigger)
                        {
                            _ = DeleteFromRedLeafAsync(automation.Name);
                            _automations.Remove(automation);
                        }
                        else
                        {
                            _ = PatchRedLeafDataAsync(automation.Name, automation);
                        }
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
                            await ReportToDiscussionAsync(automation, result, ct);

                        if (result?.Triggered == true && automation.RemoveOnTrigger)
                        {
                            _ = DeleteFromRedLeafAsync(automation.Name);
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
                            if (automation.RemoveOnTrigger)
                            {
                                _ = DeleteFromRedLeafAsync(automation.Name);
                                _automations.Remove(automation);
                            }
                            else
                            {
                                _ = PatchRedLeafDataAsync(automation.Name, automation);
                            }
                        }
                        else
                        {
                            automation.NextRun = CalculateNextRun(automation);
                        }
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
                    + $"Post your content to it using: POST http://127.0.0.1:18803/api/discussions/{discussionId}/nova-message\n"
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
                $"http://127.0.0.1:18803/api/discussions/{discussionId}/event",
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
                $"http://127.0.0.1:18803/api/discussions/{discussionId}/event",
                new { content = eventContent, source = automation.Name }, ct);
        }
        catch (Exception ex)
        {
            _log.Error("automations", $"[{automation.Name}] Failed to report: {ex.Message}");
        }
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

    // ── RedLeaf persistence ─────────────────────────────────────────────────

    private static string AutomationSlug(string name)
    {
        var sanitized = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"automation-nova-{sanitized}";
    }

    private HttpClient BuildRedLeafClient()
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        var token = jwt.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
        var http = new HttpClient
        {
            BaseAddress = new Uri(_config.Suite.RedLeaf.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    private static object BuildEntityData(Automation a) => new
    {
        automation_name = a.Name,
        description = a.Description,
        schedule = a.Schedule,
        enabled = a.Enabled,
        action_type = a.ActionType,
        action_config_json = a.ActionConfigJson,
        report_to_discussion_id = a.ReportToDiscussionId,
        remove_on_trigger = a.RemoveOnTrigger,
        expires_at = a.ExpiresAt is { } exp
            ? new DateTimeOffset(DateTime.SpecifyKind(exp, DateTimeKind.Utc)).ToString("O")
            : (string?)null,
        max_failures = a.MaxFailures,
        owner_id = a.OwnerId,
        icon = a.Icon,
        app = "nova",
    };

    /// <summary>Creates or replaces the automation entity in RedLeaf. Returns the entity ID on success.</summary>
    private async Task<Guid?> UpsertToRedLeafAsync(Automation a)
    {
        var slug = AutomationSlug(a.Name);
        try
        {
            using var http = BuildRedLeafClient();
            var body = JsonSerializer.Serialize(new
            {
                name = a.Name,
                type_slug = "automation",
                data = BuildEntityData(a),
            }, HttpJsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await http.PutAsync($"api/entities/by-slug/{Uri.EscapeDataString(slug)}", content);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warn("automations", $"RedLeaf upsert failed for {a.Name}: {(int)resp.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var idStr = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            return idStr != null && Guid.TryParse(idStr, out var id) ? id : (Guid?)null;
        }
        catch (Exception ex)
        {
            _log.Warn("automations", $"RedLeaf upsert error for {a.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Patches the entity data fields in RedLeaf. Falls back to upsert if entity ID is unknown.</summary>
    private async Task PatchRedLeafDataAsync(string name, Automation a)
    {
        if (!_entityIds.TryGetValue(name, out var entityId))
        {
            var newId = await UpsertToRedLeafAsync(a);
            if (newId.HasValue) _entityIds[name] = newId.Value;
            return;
        }

        try
        {
            using var http = BuildRedLeafClient();
            var body = JsonSerializer.Serialize(BuildEntityData(a), HttpJsonOptions);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await http.PatchAsync($"api/entities/{entityId}/data", content);
            if (!resp.IsSuccessStatusCode)
                _log.Warn("automations", $"RedLeaf patch failed for {name}: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _log.Warn("automations", $"RedLeaf patch error for {name}: {ex.Message}");
        }
    }

    /// <summary>Deletes the automation entity from RedLeaf. Looks up by slug if entity ID is unknown.</summary>
    private async Task DeleteFromRedLeafAsync(string name)
    {
        if (!_entityIds.TryGetValue(name, out var entityId))
        {
            // Fall back to slug lookup
            try
            {
                using var lookupHttp = BuildRedLeafClient();
                var slug = AutomationSlug(name);
                var lookupResp = await lookupHttp.GetAsync($"api/entities/{Uri.EscapeDataString(slug)}");
                if (!lookupResp.IsSuccessStatusCode)
                {
                    _log.Warn("automations", $"Cannot delete {name}: entity not found in RedLeaf");
                    return;
                }
                using var doc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync());
                var idStr = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (idStr == null || !Guid.TryParse(idStr, out entityId))
                {
                    _log.Warn("automations", $"Cannot delete {name}: entity ID not parseable");
                    return;
                }
                _entityIds[name] = entityId;
            }
            catch (Exception ex)
            {
                _log.Warn("automations", $"RedLeaf lookup error for {name}: {ex.Message}");
                return;
            }
        }

        try
        {
            using var http = BuildRedLeafClient();
            var resp = await http.DeleteAsync($"api/entities/{entityId}");
            if (!resp.IsSuccessStatusCode)
                _log.Warn("automations", $"RedLeaf delete failed for {name}: {(int)resp.StatusCode}");
            else
                _entityIds.Remove(name);
        }
        catch (Exception ex)
        {
            _log.Warn("automations", $"RedLeaf delete error for {name}: {ex.Message}");
        }
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            using var http = BuildRedLeafClient();
            var resp = await http.GetAsync("api/entities?type=automation&data.app=nova&limit=100");
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var items = doc.RootElement.GetProperty("items");

                if (items.GetArrayLength() == 0)
                {
                    await SeedFromFileAsync();
                }
                else
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var a = MapEntity(item);
                        if (a == null) continue;

                        if (item.TryGetProperty("id", out var idEl)
                            && Guid.TryParse(idEl.GetString(), out var id))
                            _entityIds[a.Name] = id;

                        _automations.Add(a);
                    }
                    _log.Info("automations", $"Loaded {_automations.Count} automation(s) from RedLeaf");
                }
            }
            else
            {
                _log.Warn("automations", $"Failed to load from RedLeaf ({(int)resp.StatusCode}), falling back to local file");
                LoadFromFile();
            }
        }
        catch (Exception ex)
        {
            _log.Warn("automations", $"RedLeaf load failed: {ex.Message}, falling back to local file");
            LoadFromFile();
        }

        foreach (var a in _automations.Where(a => a.Enabled))
            a.NextRun = CalculateNextRun(a);

        await EnsureBuiltInsAsync();
    }

    private void LoadFromFile()
    {
        var json = _memory.ReadMemoryFile("memory/meta/automations.json");
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var items = JsonSerializer.Deserialize<List<Automation>>(json, JsonOptions);
            if (items != null) _automations.AddRange(items);
            _log.Info("automations", $"Loaded {_automations.Count} automation(s) from local file");
        }
        catch (Exception ex) { _log.Warn("automations", $"Failed to load from file: {ex.Message}"); }
    }

    private async Task SeedFromFileAsync()
    {
        _log.Info("automations", "No automations in RedLeaf — seeding from local JSON file");
        LoadFromFile();

        foreach (var a in _automations.ToList())
        {
            var entityId = await UpsertToRedLeafAsync(a);
            if (entityId.HasValue)
                _entityIds[a.Name] = entityId.Value;
        }
        _log.Info("automations", $"Seeded {_automations.Count} automation(s) to RedLeaf");
    }

    private static Automation? MapEntity(JsonElement item)
    {
        if (!item.TryGetProperty("data", out var dataEl)) return null;

        JsonElement d;
        if (dataEl.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(dataEl.GetString()!);
            d = parsed.RootElement.Clone();
        }
        else if (dataEl.ValueKind == JsonValueKind.Object)
            d = dataEl;
        else return null;

        var name = GetStr(d, "automation_name");
        if (string.IsNullOrEmpty(name)) return null;

        // Skip tombstoned entities
        if (d.TryGetProperty("removed", out var removedEl) && removedEl.GetBoolean())
            return null;

        return new Automation
        {
            Name = name,
            Description = GetStr(d, "description") ?? "",
            Schedule = GetStr(d, "schedule") ?? "",
            Enabled = GetBool(d, "enabled") ?? true,
            RemoveOnTrigger = GetBool(d, "remove_on_trigger") ?? false,
            Icon = GetStr(d, "icon"),
            ActionType = GetStr(d, "action_type") ?? "",
            ActionConfigJson = GetStr(d, "action_config_json"),
            ReportToDiscussionId = GetStr(d, "report_to_discussion_id"),
            MaxFailures = GetInt(d, "max_failures") ?? 0,
            OwnerId = GetStr(d, "owner_id"),
            ExpiresAt = ParseUtcDateTime(GetStr(d, "expires_at")),
        };
    }

    private static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.True ? true
             : v.ValueKind == JsonValueKind.False ? false
             : (bool?)null;
    }

    private static int? GetInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;

    private static DateTime? ParseUtcDateTime(string? value) =>
        value != null && DateTimeOffset.TryParse(value, out var t) ? t.UtcDateTime : null;

    // ── Built-ins ───────────────────────────────────────────────────────────

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

    private async Task EnsureBuiltInsAsync()
    {
        var defaultOwner = ResolveDefaultOwner();

        if (_automations.All(a => a.Name != "system:backup"))
        {
            var backup = new Automation
            {
                Name = "system:backup",
                Description = "Daily memory backup",
                Schedule = "0 3 * * *",
                ActionType = "builtin:backup",
                OwnerId = defaultOwner,
                Enabled = true,
            };
            _automations.Add(backup);
            backup.NextRun = CalculateNextRun(backup);
            var id = await UpsertToRedLeafAsync(backup);
            if (id.HasValue) _entityIds[backup.Name] = id.Value;
            _log.Info("automations", "Added built-in backup automation");
        }

        var dreamingSkill = _memory.ReadMemoryFile("config/skills/dreaming.md") ?? "";
        var existingDreaming = _automations.FirstOrDefault(a => a.Name == "system:dreaming");
        if (existingDreaming == null)
        {
            var dreaming = new Automation
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
            };
            _automations.Add(dreaming);
            dreaming.NextRun = CalculateNextRun(dreaming);
            var id = await UpsertToRedLeafAsync(dreaming);
            if (id.HasValue) _entityIds[dreaming.Name] = id.Value;
            _log.Info("automations", "Added built-in dreaming automation");
        }
        else if (!string.IsNullOrEmpty(dreamingSkill))
        {
            var currentConfig = Deserialize<AiSessionConfig>(existingDreaming.ActionConfigJson);
            if (currentConfig.Prompt != dreamingSkill)
            {
                currentConfig.Prompt = dreamingSkill;
                existingDreaming.ActionConfigJson = JsonSerializer.Serialize(currentConfig, JsonOptions);
                await PatchRedLeafDataAsync(existingDreaming.Name, existingDreaming);
                _log.Info("automations", "Synced dreaming prompt from skill file");
            }
        }

        // Backfill OwnerId on any automations that lack one
        if (defaultOwner != null)
        {
            foreach (var a in _automations.Where(a => a.OwnerId == null).ToList())
            {
                a.OwnerId = defaultOwner;
                await PatchRedLeafDataAsync(a.Name, a);
                _log.Info("automations", $"Set default owner on {a.Name}");
            }
        }
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
