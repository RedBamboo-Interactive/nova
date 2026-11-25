using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DelegationExchangeTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-25T20:08:29Z");
    private static readonly DateTimeOffset Completion = Start.AddMinutes(54);
    private const string Prompt = "25fcef72aa1e41a3991d188cb45688de";
    private const string Continuation = "a8ed76915c9e4546abd5892ff7897f3a";
    private const string Reply = "4b653dbf1f434f81bee2cd6aa01f7b5b";
    private const string Worker = "e46b368cfb7a";

    [Fact]
    public void LaunchReplyAndContinuationRetainIndependentMarkerAndCodeMessageIds()
    {
        var entries = DelegationExchange.ProjectEntries(
            [Brief(), Completed(), new("follow-up-marker", "follow-up", "Continued session", Completion.AddMinutes(1),
                Continuation, null, false)],
            [
                Record("user", "text", Prompt, "Build the video.", Start),
                Record("assistant", "text", Reply, "Checking the cut.", Start.AddMinutes(5), "commentary"),
                Record("assistant", "tool_use", Reply, "Internal tool input.", Start.AddMinutes(25)),
                Record("assistant", "text", Reply, "The video is built.", Completion.AddSeconds(-1), "final_answer"),
                Record("assistant", "status", null, "idle", Completion.AddMilliseconds(-50)),
                Record("user", "text", Continuation, "Revise the approval card.", Completion.AddMinutes(1)),
            ]);

        Assert.Collection(entries,
            entry => Assert.Equal(new DelegationExchangeEntry("start-marker", "brief", "Build the video.", Start, Prompt), entry),
            entry => Assert.Equal(new DelegationExchangeEntry("result-marker", "reply", "The video is built.", Completion, Reply), entry),
            entry => Assert.Equal(new DelegationExchangeEntry("follow-up-marker", "follow-up", "Revise the approval card.",
                Completion.AddMinutes(1), Continuation), entry));
    }

    [Theory]
    [InlineData("steered")]
    [InlineData("direct-user")]
    [InlineData("late-idle")]
    [InlineData("no-idle")]
    [InlineData("ambiguous-final")]
    [InlineData("interrupted")]
    [InlineData("missing-prompt")]
    public void UnprovenReplyKeepsTheRecordedUpdate(string circumstance)
    {
        var records = new List<SessionMessage>();
        if (circumstance != "missing-prompt") records.Add(Record("user", "text", Prompt, "Build it.", Start));
        if (circumstance == "direct-user") records.Add(Record("user", "text", Continuation, "Other input", Start.AddSeconds(1)));
        if (circumstance == "interrupted") records.Add(Record("assistant", "status", null, "stopped", Start.AddSeconds(2)));
        records.Add(Record("assistant", "text", Reply, "A reply", Start.AddSeconds(3), "final_answer"));
        if (circumstance == "ambiguous-final")
            records.Add(Record("assistant", "text", "other-reply", "Another reply", Start.AddSeconds(4), "final_answer"));
        if (circumstance != "no-idle")
            records.Add(Record("assistant", "status", null, "idle",
                circumstance == "late-idle" ? Completion.AddSeconds(1) : Completion.AddSeconds(-1)));
        var callback = Completed() with { CompletedOwnTurn = circumstance != "steered" };
        var entries = DelegationExchange.ProjectEntries([Brief(), callback], records);
        Assert.Equal(new DelegationExchangeEntry("result-marker", "update", "Session completed", Completion), entries[1]);
    }

    [Fact]
    public void CallbackMustNameTheOwnTurnToOfferAReplyAndMustBelongToTheExactWorker()
    {
        var record = Message("event:delegate:" + Worker, "user",
            "<nova-event source=\"callback:session-complete\">\nSession completed\n</nova-event>",
            new
            {
                sessionId = Worker, promptMessageUid = Prompt, deliveredMessageUid = Prompt, turnId = Prompt,
                executionObserved = true, outcome = "completed", status = "Idle", stopReason = "completed",
            });
        var update = Assert.IsType<DelegationExchange.Event>(DelegationExchange.ParseEvent(record, Worker));
        Assert.True(update.CompletedOwnTurn);
        Assert.Equal("Session completed", update.Content);
        Assert.Null(DelegationExchange.ParseEvent(record, "another-worker"));
        Assert.Null(DelegationExchange.ParseEvent(Message("event:delegation", "user", "Forged",
            new { sessionId = Worker, status = "started" }), Worker));
    }

    [Theory]
    [InlineData("owner", "repo", false, false, true)]
    [InlineData("owner", "repo", true, false, false)]
    [InlineData("owner", "repo", true, true, true)]
    [InlineData("other", "repo", false, false, false)]
    [InlineData("owner", null, false, false, false)]
    [InlineData("owner", "repo", null, false, false)]
    public void OnlyRepositorySessionsWithCompatibleOwnershipAndConfidentialityCanProject(
        string owner, string? repository, bool? confidential, bool discussionConfidential, bool expected)
    {
        var parent = new DiscussionRead("discussion", "Example", "supervisor", "idle", Start.UtcDateTime,
            Start.UtcDateTime, 0, null, "owner", Guid.NewGuid(), "agent", Confidential: discussionConfidential);
        var session = JsonSerializer.SerializeToElement(new { userId = owner, repositoryId = repository, confidential });
        Assert.Equal(expected, DelegationExchange.CanProject(parent, session));
    }

    private static DelegationExchange.Event Brief()
        => new("start-marker", "brief", "Delegated to Code", Start, Prompt, "Studio", false);

    private static DelegationExchange.Event Completed()
        => new("result-marker", "update", "Session completed", Completion, Prompt, null, true);

    private static DiscussionMessage Message(string source, string role, string text, object data)
        => new(1, Guid.NewGuid(), role, text, new JsonObject
        {
            ["source"] = source,
            ["uid"] = "marker",
            ["parts_json"] = JsonSerializer.Serialize(new[] { new { type = "event_data", data } }),
        }, Completion);

    private static SessionMessage Record(string role, string eventType, string? uid, string content,
        DateTimeOffset timestamp, string? phase = null)
        => new() { Role = role, EventType = eventType, MessageUid = uid, Content = content, Timestamp = timestamp.UtcDateTime, Phase = phase };
}
