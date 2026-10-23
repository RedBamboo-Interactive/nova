using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscussionDisplayTitleTests
{
    private const string Nova = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private static readonly ResolvedDevice Device = new() { Name = "API", Type = "api", Platform = "api" };

    [Fact]
    public void UntitledLiveIsNamedByItsUtcStartDate()
    {
        var live = Discussion("780e4aed", "live", Utc("2026-10-22T03:30:14Z"));

        Assert.Equal("Live · 2026-10-22", DiscussionDisplayTitle.For(live));
    }

    [Fact]
    public void LiveFromAnotherYearCarriesItsYear()
    {
        Assert.Equal("Live · 2025-12-31",
            DiscussionDisplayTitle.For(Discussion("old", "live", Utc("2025-12-31T09:00:00Z"))));
    }

    [Fact]
    public void RenamedLiveKeepsItsTitle()
    {
        var renamed = Discussion("97370104", "live", Utc("2026-09-10T06:41:00Z"), title: "Saturday morning check-in");

        Assert.Equal("Saturday morning check-in", DiscussionDisplayTitle.For(renamed, [renamed]));
    }

    [Fact]
    public void SameDayLivesAreToldApartByStartTime()
    {
        var rotated = Discussion("12f2f6c1", "live", Utc("2026-10-08T03:30:00Z"));
        var refreshed = Discussion("3b71ed33", "live", Utc("2026-10-08T20:37:00Z"));
        var nextDay = Discussion("next", "live", Utc("2026-10-09T03:30:00Z"));
        var other = Discussion("leni", "live", Utc("2026-10-09T04:00:00Z"), agentId: "leni");
        var named = Discussion("named", "live", Utc("2026-10-09T20:00:00Z"), title: "Movie night");
        DiscussionRead[] peers = [rotated, refreshed, nextDay, other, named];

        Assert.Equal("Live · 2026-10-08 03:30", DiscussionDisplayTitle.For(rotated, peers));
        Assert.Equal("Live · 2026-10-08 20:37", DiscussionDisplayTitle.For(refreshed, peers));
        Assert.Equal("Live · 2026-10-09", DiscussionDisplayTitle.For(nextDay, peers));
    }

    [Fact]
    public void OrdinaryUntitledDiscussionsKeepTheCallersPlaceholder()
    {
        var chat = Discussion("c", "chat", Utc("2026-10-22T08:00:00Z"));
        var heartbeat = Discussion("h", HeartbeatService.DiscussionType, Utc("2026-10-22T08:00:00Z"));

        Assert.Null(DiscussionDisplayTitle.For(chat, [chat]));
        Assert.Null(DiscussionDisplayTitle.For(heartbeat, [heartbeat]));
        Assert.Equal("Fix the cache", DiscussionDisplayTitle.For(chat with { Title = "Fix the cache" }));
    }

    [Fact]
    public void ContextNamesArchivedLivesAndKeepsOrdinaryPlaceholders()
    {
        var current = Discussion("cur", "live", Utc("2026-10-23T03:30:09Z"), messages: 3);
        var archived = Discussion("780e4aed", "live", Utc("2026-10-22T03:30:14Z"), status: DiscussionStatus.Archived, messages: 763);
        var chat = Discussion("chat1", "chat", Utc("2026-10-22T08:00:00Z"), messages: 2);
        var snapshot = NovaContextBuilder.BuildSnapshot([current, archived, chat], null, null, null);

        var rendered = NovaContextBuilder.BuildFullContext(
            snapshot, "cur", Utc("2026-10-23T12:00:00Z"), Device, "api", "Nova");

        Assert.Contains("[LIVE][cur] \"Live · 2026-10-23\"", rendered);
        Assert.Contains("[780e4aed] \"Live · 2026-10-22\" . 763 msgs . archived", rendered);
        Assert.Contains("[chat1] \"(untitled)\"", rendered);
    }

    [Fact]
    public void DeltaNamesRotatedLivesAndToleratesSnapshotsWithoutStartDates()
    {
        var old = Discussion("780e4aed", "live", Utc("2026-10-22T03:30:14Z"), messages: 763);
        var persisted = NovaContextBuilder.DeserializeSnapshot(NovaContextBuilder.SerializeSnapshot(
            NovaContextBuilder.BuildSnapshot([old], null, null, null)))!;
        persisted.Discussions[0].CreatedAt = null;
        var fresh = Discussion("cur", "live", Utc("2026-10-23T03:30:09Z"), messages: 1);
        var current = NovaContextBuilder.BuildSnapshot(
            [fresh, old with { Status = DiscussionStatus.Archived }], null, null, null);

        var rendered = NovaContextBuilder.BuildDeltaContext(
            current, persisted, "cur", Utc("2026-10-23T12:00:00Z"), Device, "api", "Nova");

        Assert.Contains("New discussion [cur] \"Live · 2026-10-23\"", rendered);
        Assert.Contains("Discussion [780e4aed] \"Live · 2026-10-22\" changed status", rendered);
        Assert.DoesNotContain("renamed", rendered);
    }

    [Fact]
    public void SnapshotsPersistTheStartDateNotADerivedTitle()
    {
        var live = Discussion("cur", "live", Utc("2026-10-23T03:30:09Z"));

        var json = NovaContextBuilder.SerializeSnapshot(NovaContextBuilder.BuildSnapshot([live], null, null, null));

        Assert.Contains("\"title\":null", json);
        Assert.Contains("\"createdAt\":\"2026-10-23T03:30:09Z\"", json);
        Assert.DoesNotContain("Live ·", json);
    }

    [Fact]
    public async Task ExportHeadingsNameLivesWithinTheExportedSet()
    {
        var exporter = new ConversationExporter(
            new RedComputeClient(CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, _) => throw new NotSupportedException())),
            CallbackDeliveryTests.Proxy.Create<IDiscussions>((name, _) => name == nameof(IDiscussions.GetMessagesAsync)
                ? Task.FromResult<IReadOnlyList<DiscussionMessage>>([])
                : throw new NotSupportedException(name)));
        var rotated = Discussion("12f2f6c1", "live", Utc("2026-10-08T03:30:00Z"));
        var refreshed = Discussion("3b71ed33", "live", Utc("2026-10-08T20:37:00Z"));
        var chat = Discussion("chat1", "chat", Utc("2026-10-08T08:00:00Z"));

        var bulk = await exporter.ExportAsync([rotated, refreshed, chat], Utc("2026-10-01T00:00:00Z"));
        var single = await exporter.ExportSingleAsync(rotated);

        Assert.Contains("## Live · 2026-10-08 03:30 [12f2f6c1]", bulk);
        Assert.Contains("## Live · 2026-10-08 20:37 [3b71ed33]", bulk);
        Assert.Contains("## Untitled [chat1]", bulk);
        Assert.StartsWith("## Live · 2026-10-08 [12f2f6c1]", single);
    }

    private static DateTime Utc(string iso) => DateTime.Parse(
        iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private static DiscussionRead Discussion(
        string id, string type, DateTime createdAt, string? title = null, string? agentId = Nova,
        string status = "idle", int messages = 0) => new(
        id, title, null, status, createdAt, createdAt, messages, null, "owner", Guid.NewGuid(), agentId, type);
}
