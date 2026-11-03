using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leaf.Plugins.Nova;

/// <summary>What <c>GET /journal</c> answers: one Agent's day, computed on read from existing records.</summary>
public sealed record JournalDayResponse
{
    public required string Agent { get; init; }
    public required string Day { get; init; }
    public required string Today { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required string TimeZone { get; init; }
    public required string PreviousDay { get; init; }

    /// <summary>Stated even when null: null means the next day is still in the future.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NextDay { get; init; }

    public JournalHeader? Header { get; init; }
    public required JournalSummary Summary { get; init; }
    public required JournalCounts Counts { get; init; }
    public required IReadOnlyList<JournalEntry> Entries { get; init; }
    public required bool Partial { get; init; }
    public required IReadOnlyList<string> Unavailable { get; init; }
}

public sealed record JournalHeader(JournalOutfit? Outfit, JournalMood? Mood);

public sealed record JournalOutfit(string Id, string Name, string? AssetUrl, string? Reason, bool Nsfw);

/// <summary>Read from the Agent's mood file only when that file is dated this day; there is no mood history.</summary>
public sealed record JournalMood(string? Energy, string? Vibe);

public sealed record JournalSummary(
    JournalLiveSummary? Live, int? DiscordOutgoing, IReadOnlyList<JournalBackgroundCount> Background);

public sealed record JournalLiveSummary(string DiscussionId, int Messages);

public sealed record JournalBackgroundCount(string Group, int Count);

/// <summary>Always derived from the entries' filters; never maintained by hand.</summary>
public sealed record JournalCounts(int All, int Own, int WithYou, int Scheduled, int Ticks, int Failed);

public static class JournalKinds
{
    public const string Thread = "thread";
    public const string Tick = "tick";
    public const string Automation = "automation";
    public const string AutomationGroup = "automation-group";
}

public static class JournalCauses
{
    public const string Asked = "asked";
    public const string Own = "own";
    public const string WithYou = "with-you";
    public const string Scheduled = "scheduled";
    public const string Tick = "tick";
    public const string Unknown = "unknown";
}

public static class JournalFilters
{
    public const string Own = "own";
    public const string WithYou = "with-you";
    public const string Scheduled = "scheduled";
    public const string Ticks = "ticks";
    public const string Failed = "failed";
}

public sealed record JournalEntry
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Cause { get; init; }
    public bool Inferred { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Title { get; init; }
    /// <summary>
    /// A thread's is its discussion's: failed delegated jobs are ordinary, and are counted in
    /// <see cref="FailedJobs"/>. Only an automation is ever <c>failed</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>The Agent's own words, verbatim Markdown. Absent when there are none; never generated.</summary>
    public string? Text { get; init; }
    public string? TextMessageUid { get; init; }

    /// <summary>A turn that ended in the silent settle.</summary>
    public bool Silent { get; init; }
    public string? DiscussionId { get; init; }
    public string? AutomationId { get; init; }
    public required IReadOnlyList<string> Filters { get; init; }
    public int FailedJobs { get; init; }
    public JournalRequest? Request { get; init; }
    public JournalStartedFrom? StartedFrom { get; init; }
    public IReadOnlyList<JournalLink> Links { get; init; } = [];
    public IReadOnlyList<JournalGroupLine> Lines { get; init; } = [];
    public IReadOnlyList<JournalSession> Sessions { get; init; } = [];
    public IReadOnlyList<JournalJobRef> Jobs { get; init; } = [];
    public IReadOnlyList<JournalArtifact> Artifacts { get; init; } = [];
    public IReadOnlyList<JournalCommit> Commits { get; init; } = [];
}

/// <summary>"He asked for this there": the message a signed Agent named when it started this work.</summary>
public sealed record JournalRequest(string DiscussionId, string MessageUid, string? Quote, DateTimeOffset? At);

public sealed record JournalStartedFrom(string DiscussionId, string? Title, string? MessageUid, bool Inferred);

public sealed record JournalLink(
    string Kind, string? DiscussionId = null, string? MessageUid = null, string? Label = null,
    string? JobId = null, string? AutomationId = null, bool Inferred = false);

public sealed record JournalGroupLine(
    DateTimeOffset At, string Title, string Status, string JobId, string? AutomationId, string? Text);

public sealed record JournalSession(string SessionId, string? Repository, string? Title, string Status);

public sealed record JournalJobRef(string Id, string? Name, string Status, string? Capability);

public sealed record JournalArtifact(
    string Key, string Kind, string Target, string? Title, string? MediaType, string? MessageUid, DateTimeOffset? At);

public sealed record JournalCommit(string Hash, string ShortHash, string? Message, string Repository, string? Url);

/// <summary>What <c>GET /journal/days</c> answers: the calendar's dots for one month.</summary>
public sealed record JournalMonthResponse(
    string Agent, string Month, string Today, IReadOnlyList<JournalCalendarDay> Days,
    bool Partial, IReadOnlyList<string> Unavailable);

public sealed record JournalCalendarDay(string Day, bool Own, bool WithYou, bool Failed);

public static class JournalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
