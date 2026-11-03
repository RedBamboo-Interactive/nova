using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// "He asked for this there": the only new write the Journal needs, stamped with the sender so the
/// server's claim stays in one place, and refused from anyone but a signed Agent that can read it.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    private const string Asked = "beadfeed";
    private const string AskedUid = "message-uid-1";

    private static RequestedInBody Reference(string discussion = Asked, string uid = AskedUid)
        => new() { DiscussionId = discussion, MessageUid = uid };

    [Fact]
    public async Task A_note_stamps_where_it_was_asked_with_the_sender_and_the_record_reads_it_back()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        AddOrigin(world, title: "Where it was asked", id: Asked);

        var (status, _) = await PostAsync(world, SignedAgent(world), new DiscussionEventRequest
        {
            Content = "Starting.", RequestedIn = Reference(),
        });

        Assert.Equal(200, status);
        var record = Assert.Single(world.EventRecords());
        var sender = EventSender.Read(record.Metadata)!;
        Assert.Equal(Origin, sender.DiscussionId);
        Assert.Equal(new RequestReference(Asked, AskedUid), sender.RequestedIn);
        Assert.Equal(JournalCauses.Asked, JournalProjection.DecideCause(JournalProjection.FromOverlay(record), false).Cause);
    }

    [Fact]
    public async Task A_note_without_one_stamps_nothing_new()
    {
        using var world = new World("session-1");
        AddOrigin(world);

        await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "x" });

        var record = Assert.Single(world.EventRecords());
        Assert.Null(EventSender.Read(record.Metadata)!.RequestedIn);
        Assert.False(record.Metadata.ContainsKey("request_discussion_id"));
        Assert.False(record.Metadata.ContainsKey("request_message_uid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("app")]
    [InlineData("user")]
    public async Task Only_a_signed_Agent_can_state_where_it_was_asked(string? kind)
    {
        using var world = new World("session-1");
        AddOrigin(world, id: Asked);
        var user = kind is null ? Person() : Signed(kind, "someone");

        var (status, body) = await PostAsync(world, user, new DiscussionEventRequest { Content = "x", RequestedIn = Reference() });

        Assert.Equal(403, status);
        Assert.Equal("requested_in_agent_only", body.GetProperty("error").GetString());
        Assert.Empty(world.EventRecords());
    }

    [Fact]
    public async Task A_discussion_the_Agent_cant_read_is_refused_and_answers_like_a_missing_one()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        world.Store.CreateDiscussion("Someone else's", AgentOf(world), new JsonObject
        {
            ["discussion_id"] = Asked, ["app"] = "nova", ["type"] = "chat", ["owner_id"] = "someone-else",
            ["confidential"] = false, ["session_id"] = "other-session", ["title"] = "Someone else's",
        });

        // A beneficiary who isn't the owner, so the discussion is out of reach for this execution.
        var agent = Signed("agent", AgentOf(world), sub: "person-1");
        var (status, body) = await PostAsync(world, agent, new DiscussionEventRequest { Content = "x", RequestedIn = Reference() });
        var (missingStatus, missingBody) = await PostAsync(world, agent, new DiscussionEventRequest { Content = "x", RequestedIn = Reference("00000000") });

        Assert.Equal(403, status);
        Assert.Equal("requested_in_unreadable", body.GetProperty("error").GetString());
        Assert.Equal(status, missingStatus);
        Assert.Equal(body.ToString(), missingBody.ToString());
        Assert.Empty(world.EventRecords());
    }

    [Fact]
    public async Task A_confidential_discussion_cant_be_cited_from_outside_it()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        AddOrigin(world, title: "Secret", confidential: true, id: Asked);

        var (status, _) = await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "x", RequestedIn = Reference() });

        Assert.Equal(403, status);
        Assert.Empty(world.EventRecords());
    }

    [Theory]
    [InlineData("", AskedUid)]
    [InlineData(Asked, "")]
    public async Task A_reference_needs_both_halves(string discussion, string uid)
    {
        using var world = new World("session-1");
        AddOrigin(world);

        var (status, body) = await PostAsync(world, SignedAgent(world), new DiscussionEventRequest { Content = "x", RequestedIn = Reference(discussion, uid) });

        Assert.Equal(400, status);
        Assert.Equal("invalid_requested_in", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Opening_a_discussion_stamps_the_reference_on_its_brief()
    {
        using var world = new World("session-1");
        AddOrigin(world);
        AddOrigin(world, title: "Where it was asked", id: Asked);
        var request = Brief();
        request.RequestedIn = Reference();

        var (status, body) = await OpenAsync(world, SignedAgent(world), request);

        Assert.Equal(200, status);
        var opened = Opened(world, body)!;
        var record = world.EventRecords().Single(item => item.DiscussionId == opened.EntityId);
        Assert.Equal(new RequestReference(Asked, AskedUid), EventSender.Read(record.Metadata)!.RequestedIn);
    }

    [Fact]
    public async Task Opening_refuses_before_anything_exists()
    {
        using var world = new World("session-1");
        AddOrigin(world, id: Asked);
        var before = (await world.Discussions.ListAsync()).Count;

        var person = Brief();
        person.RequestedIn = Reference();
        var (personStatus, _) = await OpenAsync(world, Person(), person);

        var noBrief = new CreateDiscussionRequest { Title = "No brief", RequestedIn = Reference() };
        var (noBriefStatus, noBriefBody) = await OpenAsync(world, SignedAgent(world), noBrief);

        var unreadable = Brief();
        unreadable.RequestedIn = Reference("00000000");
        var (unreadableStatus, _) = await OpenAsync(world, SignedAgent(world), unreadable);

        Assert.Equal(403, personStatus);
        Assert.Equal(400, noBriefStatus);
        Assert.Equal("requested_in_without_brief", noBriefBody.GetProperty("error").GetString());
        Assert.Equal(403, unreadableStatus);
        Assert.Equal(before, (await world.Discussions.ListAsync()).Count);
    }
}
