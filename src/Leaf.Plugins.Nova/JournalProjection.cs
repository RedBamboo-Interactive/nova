using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>One journal day: 04:00 to 04:00 in the zone, so late work stays with its evening.</summary>
public readonly record struct JournalWindow(DateOnly Day, DateTimeOffset Start, DateTimeOffset End)
{
    public const int StartHour = 4;

    public static JournalWindow For(DateOnly day, TimeZoneInfo zone)
        => new(day, StartOf(day, zone), StartOf(day.AddDays(1), zone));

    /// <summary>The day an instant belongs to: before 04:00 local it is still yesterday.</summary>
    public static DateOnly DayOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < StartHour ? day.AddDays(-1) : day;
    }

    public bool Contains(DateTimeOffset at) => at >= Start && at < End;

    // By zone, never by a fixed offset. 04:00 falls in no European transition; a zone where it does
    // gets the first valid instant of the gap, and the earlier of two readings of an overlap.
    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(StartHour, 0), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>The first record of a discussion, overlay or canonical, in the terms the cause rules read.</summary>
public sealed record JournalFirstRecord(
    string? Source, string Role, string? Content, EventSender? Stamped, string? Uid, DateTimeOffset At);

/// <summary>A discussion's cause: decided once from its true first record, which never changes.</summary>
public sealed record JournalCauseFacts(
    string Cause, bool Inferred, RequestReference? Request, JournalFirstRecord? First);

/// <summary>One note: an event in one discussion saying which other discussion it came from.</summary>
public sealed record JournalNote(
    string FromDiscussionId, string? FromTitle, string ToDiscussionId, string? MessageUid, DateTimeOffset At, bool Inferred);

/// <summary>One turn of a Heartbeat discussion: everything an assistant message uid wrote that day.</summary>
public sealed record JournalTurn(
    string MessageUid, DateTimeOffset At, string? Text, bool Silent, string Trigger);

public sealed record JournalAutomationItem(
    string JobId, string AutomationId, string Title, DateTimeOffset At, string Status, string? Text,
    int FailedJobs, IReadOnlyList<JournalJobRef> Jobs);

/// <summary>
/// The Journal's rules, as pure functions over records so each can be replayed deterministically.
/// Nothing here reads a service, and nothing authored is ever invented: text is copied or absent.
/// </summary>
public static partial class JournalProjection
{
    public const int TitleLength = 90;
    public const int QuoteLength = 160;

    private static readonly HashSet<string> InstanceSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "nova-instance", "initiative", EventSender.DefaultSource,
    };

    private static readonly HashSet<string> AutomationSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "automation", "scheduled",
    };

    // Same shapes as the web's agent-note parser: an Agent slug may itself hold hyphens and hex, and
    // the discussion is always the last eight hex digits.
    [GeneratedRegex("^(?:event:)?[a-z][a-z0-9-]*?-(?<id>[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstanceSource();

    [GeneratedRegex("^(?:event:)?(?:discussion:)?(?<id>[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareDiscussionSource();

    [GeneratedRegex("""^\s*<nova-event\b[^>]*\bsource="(?<source>[^"]*)"[^>]*>""", RegexOptions.CultureInvariant)]
    private static partial Regex FramedEvent();

    [GeneratedRegex("""^\s*<heartbeat-tick\b""", RegexOptions.CultureInvariant)]
    private static partial Regex BareTick();

    [GeneratedRegex("(?<![0-9a-zA-Z])[0-9a-f]{7,40}(?![0-9a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex HexWord();

    /// <summary>The discussion a source names, for the sources that name one.</summary>
    public static string? SenderOfSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var match = InstanceSource().Match(source);
        if (!match.Success) match = BareDiscussionSource().Match(source);
        return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
    }

    private static string? BareSource(string? source)
        => source?.StartsWith("event:", StringComparison.Ordinal) == true ? source["event:".Length..] : source;

    /// <summary>The cause rules, in order. Job cause is deliberately no input: it was wrong for older initiatives.</summary>
    public static JournalCauseFacts DecideCause(JournalFirstRecord? first, bool startedByAutomation)
    {
        if (first is null)
            return new(startedByAutomation ? JournalCauses.Scheduled : JournalCauses.Unknown, false, null, null);

        if (first.Stamped is { } stamped)
            return stamped.RequestedIn is { } request
                ? new(JournalCauses.Asked, false, request, first)
                : new(JournalCauses.Own, false, null, first);

        var source = BareSource(first.Source);
        var isEvent = first.Source?.StartsWith("event:", StringComparison.Ordinal) == true;
        if (isEvent && source is not null && (InstanceSources.Contains(source) || SenderOfSource(source) is not null))
            return new(JournalCauses.Own, true, null, first);

        if (first.Role == "user" && first.Source is "user-message" or "queued-user-message")
            return new(JournalCauses.WithYou, false, null, first);

        if (startedByAutomation || (isEvent && source is not null && AutomationSources.Contains(source)))
            return new(JournalCauses.Scheduled, false, null, first);

        // The Agent spoke first and nothing recorded why; an automation's delivery has been ruled out above.
        if (first.Role == "assistant" && first.Source == "nova-message")
            return new(JournalCauses.Own, true, null, first);

        return new(JournalCauses.Unknown, false, null, first);
    }

    /// <summary>
    /// The canonical half of a first record, reading its frame when it has one. A canonical user turn
    /// that isn't framed is plain text from the owner.
    /// </summary>
    public static JournalFirstRecord FromCanonical(SessionMessage message)
    {
        var at = At(message);
        if (message.Role != "user") return new("session-transcript", message.Role, message.Content, null, message.MessageUid, at);
        var frame = message.Content is null ? Match.Empty : FramedEvent().Match(message.Content);
        return new(frame.Success ? $"event:{frame.Groups["source"].Value}" : "user-message",
            "user", message.Content, null, message.MessageUid, at);
    }

    public static JournalFirstRecord FromOverlay(DiscussionMessage message)
        => new(Str(message.Metadata, "source"), message.Role, message.Content,
            EventSender.Read(message.Metadata), Str(message.Metadata, "uid"), message.CreatedAt);

    /// <summary>
    /// The earlier of the two streams, and the overlay's record when both describe one message: only
    /// the overlay carries the server's stamp, so a canonical copy of an event must not hide it.
    /// </summary>
    public static JournalFirstRecord? PickFirst(
        IReadOnlyList<DiscussionMessage> overlayHead, SessionMessage? canonicalFirst)
    {
        var overlays = overlayHead.Where(message => message.Role != "system").ToList();
        if (canonicalFirst is null) return overlays.Count == 0 ? null : FromOverlay(overlays[0]);
        if (overlays.Count == 0) return FromCanonical(canonicalFirst);

        if (!string.IsNullOrEmpty(canonicalFirst.MessageUid)
            && overlays.FirstOrDefault(message => Str(message.Metadata, "uid") == canonicalFirst.MessageUid) is { } same)
            return FromOverlay(same);

        return overlays[0].CreatedAt <= At(canonicalFirst) ? FromOverlay(overlays[0]) : FromCanonical(canonicalFirst);
    }

    /// <summary>A record's own time, falling back to when it was stored.</summary>
    public static DateTimeOffset At(SessionMessage message)
        => message.Timestamp > DateTime.MinValue
            ? new DateTimeOffset(DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Utc))
            : message.RecordCreatedAt ?? DateTimeOffset.MinValue;

    public static bool IsAssistantText(SessionMessage message)
        => message.Role == "assistant" && message.EventType == "text" && !string.IsNullOrWhiteSpace(message.Content);

    /// <summary>What a reader would see in the chat; lifecycle and thinking records are no activity.</summary>
    public static bool IsVisible(SessionMessage message) => message.EventType switch
    {
        "text" => !string.IsNullOrWhiteSpace(message.Content) && !NovaNoReply.IsMarker(message.Content),
        "tool_use" or "tool_result" => true,
        _ => false,
    };

    /// <summary>The discussion's last words that day, skipping a silent settle.</summary>
    public static SessionMessage? LastSpoken(IEnumerable<SessionMessage> window)
        => window.LastOrDefault(message => IsAssistantText(message) && !NovaNoReply.IsMarker(message.Content));

    /// <summary>
    /// One turn per assistant message uid. A turn's text is its last assistant text; when that is the
    /// silent marker the turn stayed silent, whatever it said on the way there.
    /// </summary>
    public static IReadOnlyList<JournalTurn> Turns(IEnumerable<SessionMessage> window)
    {
        var turns = new List<JournalTurn>();
        var order = new List<string>();
        var records = new Dictionary<string, List<SessionMessage>>(StringComparer.Ordinal);
        var triggers = new Dictionary<string, SessionMessage?>(StringComparer.Ordinal);
        SessionMessage? pending = null;
        foreach (var message in window)
        {
            if (message.Role == "user" && message.EventType == "text")
            {
                pending = message;
                continue;
            }
            if (message.Role != "assistant" || string.IsNullOrEmpty(message.MessageUid)) continue;
            if (!records.TryGetValue(message.MessageUid, out var list))
            {
                records[message.MessageUid] = list = [];
                order.Add(message.MessageUid);
                triggers[message.MessageUid] = pending;
                pending = null;
            }
            list.Add(message);
        }

        foreach (var uid in order)
        {
            var list = records[uid];
            var trigger = triggers[uid];
            var last = list.LastOrDefault(IsAssistantText);
            var silent = last is not null && NovaNoReply.IsMarker(last.Content);
            turns.Add(new(uid, trigger is null ? At(list[0]) : At(trigger),
                silent ? null : last?.Content, silent, TriggerOf(trigger, list[0])));
        }
        return turns;
    }

    private static string TriggerOf(SessionMessage? trigger, SessionMessage first)
    {
        if (trigger?.Content is { } content)
        {
            if (BareTick().IsMatch(content)) return "tick";
            var frame = FramedEvent().Match(content);
            if (frame.Success)
                return frame.Groups["source"].Value.Equals("heartbeat-tick", StringComparison.OrdinalIgnoreCase) ? "tick" : "note";
            return "message";
        }
        return first.TurnOrigin is not null ? "background" : "unknown";
    }

    /// <summary>The first sentence of the Agent's own text, truncated. Nothing is composed.</summary>
    public static string FirstSentence(string text, int length = TitleLength)
    {
        var line = text.Trim();
        var end = line.IndexOf('\n');
        if (end >= 0) line = line[..end].TrimEnd();
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] is not ('.' or '!' or '?') || !char.IsWhiteSpace(line[i + 1])) continue;
            line = line[..(i + 1)];
            break;
        }
        return Truncate(line, length);
    }

    public static string Truncate(string text, int length)
        => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    /// <summary>A quoted message: its first line, truncated.</summary>
    public static string? Quote(string? content)
    {
        var stripped = ConversationExporter.StripInjectedTags(content ?? "").Trim();
        if (stripped.Length == 0) return null;
        var end = stripped.IndexOf('\n');
        return Truncate(end >= 0 ? stripped[..end].TrimEnd() : stripped, QuoteLength);
    }

    /// <summary>Reads who sent an overlay event, from its stamp or, marked inferred, from an older source.</summary>
    public static JournalNote? NoteOf(DiscussionMessage message, string toDiscussionId)
    {
        var source = Str(message.Metadata, "source");
        if (source?.StartsWith("event:", StringComparison.Ordinal) != true) return null;
        var uid = Str(message.Metadata, "uid");
        if (EventSender.Read(message.Metadata) is { } stamped)
            return stamped.DiscussionId is { } from && from != toDiscussionId
                ? new(from, stamped.Title, toDiscussionId, uid, message.CreatedAt, false)
                : null;
        return SenderOfSource(source) is { } sender && sender != toDiscussionId
            ? new(sender, null, toDiscussionId, uid, message.CreatedAt, true)
            : null;
    }

    /// <summary>The turn a note left during: the last one that started at or before it.</summary>
    public static JournalTurn? TurnAt(IReadOnlyList<JournalTurn> turns, DateTimeOffset at)
        => turns.LastOrDefault(turn => turn.At <= at);

    /// <summary>Hex words in the Agent's own text: candidates only, until a repository resolves one.</summary>
    public static IReadOnlyList<string> HashCandidates(IEnumerable<string?> texts, int limit = 30)
    {
        var found = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in HexWord().Matches(text))
            {
                // Seven digits with no letter are a number far more often than a hash.
                if (match.Value.All(char.IsAsciiDigit) || found.Contains(match.Value)) continue;
                found.Add(match.Value);
                if (found.Count >= limit) return found;
            }
        }
        return found;
    }

    /// <summary>A remote as a browsable base, credentials stripped; null when it isn't one.</summary>
    public static string? RemoteBase(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var value = remote.Trim();
        var scp = Regex.Match(value, "^[^@/]+@(?<host>[^:/]+):(?<path>.+)$", RegexOptions.CultureInvariant);
        if (scp.Success) value = $"https://{scp.Groups["host"].Value}/{scp.Groups["path"].Value}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Length <= 1 ? null : $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>
    /// Runs of automations with nothing between them collapse into one
    /// entry. A failed one stands alone and so ends a run: it is never grouped away.
    /// </summary>
    public static IReadOnlyList<JournalEntry> GroupAutomations(IReadOnlyList<JournalEntry> ordered)
    {
        var result = new List<JournalEntry>();
        var run = new List<JournalEntry>();

        void Flush()
        {
            if (run.Count == 1) result.Add(run[0]);
            else if (run.Count > 1) result.Add(Group(run));
            run.Clear();
        }

        foreach (var entry in ordered)
        {
            if (entry.Kind != JournalKinds.Automation || entry.Status == "failed")
            {
                Flush();
                result.Add(entry);
                continue;
            }
            run.Add(entry);
        }
        Flush();
        return result;
    }

    private static JournalEntry Group(IReadOnlyList<JournalEntry> run)
    {
        var first = run[0];
        return new JournalEntry
        {
            Id = $"group:{first.Id}",
            Kind = JournalKinds.AutomationGroup,
            Cause = JournalCauses.Scheduled,
            At = first.At,
            Title = "Scheduled",
            Status = run.Any(entry => entry.Status == "running") ? "running" : "completed",
            Filters = [JournalFilters.Scheduled],
            // No text of its own: no run speaks for the others, and each says its outcome on its line.
            Lines = run.Select(entry => new JournalGroupLine(
                entry.At, entry.Title, entry.Status, entry.Jobs.FirstOrDefault()?.Id ?? "",
                entry.AutomationId, string.IsNullOrWhiteSpace(entry.Text) ? null : FirstSentence(entry.Text))).ToList(),
            Jobs = run.SelectMany(entry => entry.Jobs).ToList(),
            Artifacts = run.SelectMany(entry => entry.Artifacts).ToList(),
        };
    }

    public static JournalCounts Count(IReadOnlyList<JournalEntry> entries)
    {
        int Of(string filter) => entries.Count(entry => entry.Filters.Contains(filter));
        return new(entries.Count, Of(JournalFilters.Own), Of(JournalFilters.WithYou),
            Of(JournalFilters.Scheduled), Of(JournalFilters.Ticks), Of(JournalFilters.Failed));
    }

    /// <summary>The filters an entry answers to; everything under Failed keeps its own filter too.</summary>
    public static IReadOnlyList<string> FiltersOf(string kind, string cause, int failedJobs, bool noteToLive)
    {
        var filters = new List<string>();
        switch (kind)
        {
            case JournalKinds.Tick:
                filters.Add(JournalFilters.Ticks);
                if (noteToLive) filters.Add(JournalFilters.Own);
                break;
            case JournalKinds.Automation or JournalKinds.AutomationGroup:
                filters.Add(JournalFilters.Scheduled);
                break;
            default:
                switch (cause)
                {
                    case JournalCauses.Own: filters.Add(JournalFilters.Own); break;
                    case JournalCauses.WithYou or JournalCauses.Asked: filters.Add(JournalFilters.WithYou); break;
                    case JournalCauses.Scheduled: filters.Add(JournalFilters.Scheduled); break;
                }
                break;
        }
        if (failedJobs > 0) filters.Add(JournalFilters.Failed);
        return filters;
    }

    /// <summary>A thread's status is its discussion's. A delegated job failing and being retried is ordinary.</summary>
    public static string ThreadStatus(string discussionStatus)
    {
        return discussionStatus switch
        {
            "thinking" => "running",
            DiscussionStatus.Archived or DiscussionStatus.Archiving => "archived",
            "stopped" => "stopped",
            _ => "idle",
        };
    }

    /// <summary>The energy and vibe of a mood file, only when the file is dated this journal day.</summary>
    public static JournalMood? ReadMood(string? content, JournalWindow window)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        DateTimeOffset? updated = null;
        string? energy = null, vibe = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (updated is null && line.StartsWith("Updated:", StringComparison.OrdinalIgnoreCase))
                updated = ParseUpdated(line["Updated:".Length..].Trim());
            else if (energy is null && line.StartsWith("Energy:", StringComparison.OrdinalIgnoreCase))
                energy = NullIfEmpty(line["Energy:".Length..]);
            else if (vibe is null && line.StartsWith("Vibe:", StringComparison.OrdinalIgnoreCase))
                vibe = NullIfEmpty(line["Vibe:".Length..]);
        }
        if (updated is not { } at || !window.Contains(at) || (energy is null && vibe is null)) return null;
        return new(energy, vibe);
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ParseUpdated(string value)
    {
        var text = value.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase) ? value[..^4] + "Z" : value;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// An automation session's outcome, verbatim. Dreaming sessions end in a machine-shaped
    /// <c>{"summary":...}</c>; the words are the summary's, so that is what is shown.
    /// </summary>
    public static string? OutcomeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{')) return text;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.TryGetProperty("summary", out var summary)
                && summary.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(summary.GetString())
                    ? summary.GetString()
                    : text;
        }
        catch (JsonException) { return text; }
    }

    /// <summary>The automation's own name, without the internal prefix system ones carry.</summary>
    public static string AutomationTitle(string? name, string? fallback)
    {
        var value = (name ?? fallback ?? "Automation").Trim();
        return value.StartsWith("system:", StringComparison.OrdinalIgnoreCase) ? value["system:".Length..] : value;
    }

    public static string JobStatusWord(string? status) => status switch
    {
        "Failed" or "TimedOut" => "failed",
        "Queued" or "Running" => "running",
        "Skipped" => "skipped",
        "Cancelled" => "cancelled",
        _ => "completed",
    };

    /// <summary>Jobs the footer rolls up by name; the rest are "Other" unless an entry represents them.</summary>
    public static string? BackgroundGroup(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.StartsWith("Sentinel", StringComparison.Ordinal)) return "Sentinel";
        if (name.StartsWith("Generate discussion title", StringComparison.Ordinal)) return "Titles";
        if (name.StartsWith("Pixoo", StringComparison.Ordinal)) return "Pixoo";
        return null;
    }

    public const string DiscordOutboundJob = "Sentinel: Discord outbound";

    private static string? Str(JsonObject metadata, string key)
        => metadata[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
