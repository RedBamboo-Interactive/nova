using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// fcd8e82c: what an Agent sends shows in the discussion it wrote from, for the reader only. The
/// sending session never gets its own words back, and recording it can't touch the note itself.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    private static async Task<List<DiscussionMessage>> SentRecordsAsync(World world, string discussionId = Origin)
    {
        var from = await world.Discussions.GetAsync(discussionId, CancellationToken.None);
        return world.Stream.Snapshot()
            .Where(r => r.DiscussionId == from!.EntityId && r.Metadata["source"]?.GetValue<string>() == $"event:{SentNote.Source}")
            .ToList();
    }

    private static JsonNode SentData(DiscussionMessage record)
        => JsonNode.Parse(record.Metadata["parts_json"]!.GetValue<string>())!.AsArray()
            .First(part => part?["type"]?.GetValue<string>() == "event_data")!["data"]!;

    [Fact]
    public async Task A_note_to_another_discussion_shows_where_it_was_sent_from()
    {
        using var world = new World("session-1");
        AddOrigin(world);

        var (status, _) = await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "The build is done." });

        Assert.Equal(200, status);
        var record = Assert.Single(await SentRecordsAsync(world));
        Assert.Equal("system", record.Role);
        Assert.Equal("The build is done.", record.Content);
        var data = SentData(record);
        Assert.Equal(world.Discussion().Id, data["targetDiscussionId"]!.GetValue<string>());
        Assert.Equal(world.Discussion().AgentId, data["targetAgentId"]!.GetValue<string>());
        Assert.False(data["failed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_sent_note_reaches_only_the_session_it_was_written_to()
    {
        using var world = new World("session-1");
        AddOrigin(world);

        await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "The build is done." });

        var delivered = Assert.Single(world.Gateway.Messages);
        Assert.Equal("session-1", delivered.Session);
        Assert.DoesNotContain(world.Gateway.Messages, m => m.Session == "origin-session");
    }

    [Fact]
    public async Task A_sent_record_states_no_sender_so_it_can_never_read_as_a_note_received()
    {
        using var world = new World("session-1");
        AddOrigin(world);

        await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "The build is done." });

        Assert.Null(EventSender.Read(Assert.Single(await SentRecordsAsync(world)).Metadata));
    }

    [Fact]
    public async Task A_note_to_its_own_discussion_shows_once()
    {
        using var world = new World("session-1");
        var own = world.Discussion().Id;

        await PostAsync(world, SignedAgent(world, discussion: own), new DiscussionEventRequest { Content = "Note to self." });

        Assert.Empty(await SentRecordsAsync(world, own));
        Assert.Single(world.EventRecords());
    }

    [Theory]
    [InlineData("person")]
    [InlineData("flow")]
    public async Task Only_an_Agent_writing_from_a_discussion_records_one(string caller)
    {
        using var world = new World("session-1");
        AddOrigin(world);
        var user = caller == "person" ? Person() : SignedAgent(world, discussion: null);

        await PostAsync(world, user, new DiscussionEventRequest { Content = "Hello.", Source = "automation" });

        Assert.Empty(await SentRecordsAsync(world));
    }

    [Fact]
    public async Task A_retried_note_shows_once()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        var request = new DiscussionEventRequest { Content = "The build is done.", IdempotencyKey = "retry-1" };

        await PostAsync(world, SignedAgent(world), request);
        await PostAsync(world, SignedAgent(world), request);

        Assert.Single(await SentRecordsAsync(world));
    }

    [Fact]
    public async Task A_confidential_target_gives_the_sender_no_title_id_or_Agent()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        await world.Store.PatchAsync(world.Store.DiscussionId, new JsonObject { ["confidential"] = true });

        await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "The build is done." });

        var data = SentData(Assert.Single(await SentRecordsAsync(world)));
        Assert.Null(data["targetDiscussionId"]);
        Assert.Null(data["targetTitle"]);
        Assert.Null(data["targetAgentId"]);
    }

    [Fact]
    public async Task A_closed_sender_stays_closed_and_its_note_still_goes_out()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        var from = await world.Discussions.GetAsync(Origin, CancellationToken.None);
        await world.Store.PatchAsync(from!.EntityId, new JsonObject { ["status"] = DiscussionStatus.Archived });

        var (status, _) = await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "The build is done." });

        Assert.Equal(200, status);
        Assert.Single(world.Gateway.Messages);
        Assert.Empty(await SentRecordsAsync(world));
    }

    [Fact]
    public async Task A_brief_shows_where_the_discussion_was_opened_from()
    {
        using var world = new World("session-1");
        AddOrigin(world);

        var (_, opened) = await OpenAsync(world, SignedAgent(world), new CreateDiscussionRequest { Title = "New work", Brief = "Start on this." });

        var record = Assert.Single(await SentRecordsAsync(world));
        Assert.Equal("Start on this.", record.Content);
        Assert.Equal(opened.GetProperty("id").GetString(), SentData(record)["targetDiscussionId"]!.GetValue<string>());
    }

    [Fact]
    public void A_note_that_was_refused_or_lost_reads_as_failed_and_a_queued_one_does_not()
    {
        Assert.True(SentNote.Failed(EventDelivery.Failed));
        Assert.True(SentNote.Failed(EventDelivery.Undeliverable));
        Assert.True(SentNote.Failed(EventDelivery.Denied));
        Assert.False(SentNote.Failed(EventDelivery.Delivered));
        Assert.False(SentNote.Failed(EventDelivery.Queued));
        Assert.False(SentNote.Failed(EventDelivery.Reused));
    }
}
