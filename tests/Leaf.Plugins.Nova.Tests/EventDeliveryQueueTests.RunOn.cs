using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.Nova.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// "Run on" as a discussion setting (Phase 2 design 3.1, 3.2): the node its new sessions run on, chosen at
/// creation (<c>nodeId</c>) or later through PUT /discussions/{id}/node, stored as <c>node_id</c>, shown on the
/// discussion as <c>nodeId</c> and announced with discussion.changed. Automatic is null and stores nothing.
/// </summary>
public sealed partial class EventDeliveryQueueTests
{
    [Fact]
    public async Task A_discussion_opened_on_a_node_keeps_it_for_its_sessions()
    {
        using var world = new World("session-1");

        var (status, chosen) = await OpenAsync(world, Person(),
            JsonSerializer.Deserialize<CreateDiscussionRequest>("""{"title":"Render","nodeId":"node_01STUDIO"}""", Web)!);
        var (_, automatic) = await OpenAsync(world, Person(), new CreateDiscussionRequest { Title = "Plain" });

        Assert.Equal(200, status);
        Assert.Equal("node_01STUDIO", chosen.GetProperty("nodeId").GetString());
        Assert.Equal(JsonValueKind.Null, automatic.GetProperty("nodeId").ValueKind);
        var stored = await world.Discussions.GetAsync(chosen.GetProperty("id").GetString()!);
        Assert.Equal("node_01STUDIO", stored!.RunOnNodeId);
        // Automatic writes no key at all: the record is what it was before nodes could be chosen.
        var plain = await world.Discussions.GetAsync(automatic.GetProperty("id").GetString()!);
        Assert.Null(plain!.RunOnNodeId);
        Assert.False(world.Store.Get(plain.EntityId).Data.ContainsKey(DiscussionStore.RunOnNodeKey));
    }

    [Theory]
    [InlineData("""{"nodeId":""}""")]
    [InlineData("""{"nodeId":"   "}""")]
    [InlineData("""{"nodeId":"a\u0000b"}""")]
    public async Task A_malformed_node_id_refuses_the_creation_before_anything_exists(string body)
    {
        using var world = new World("session-1");
        var before = world.Published.Count;

        var (status, _) = await OpenAsync(world, Person(), JsonSerializer.Deserialize<CreateDiscussionRequest>(body, Web)!);

        Assert.Equal(400, status);
        Assert.Equal(before, world.Published.Count);
    }

    [Fact]
    public async Task The_person_picks_a_node_and_returns_to_automatic()
    {
        using var world = new World("session-1");

        var (pickedStatus, picked) = await SetNodeAsync(world, Person(), "\"node_01STUDIO\"");
        Assert.Equal(200, pickedStatus);
        Assert.Equal("node_01STUDIO", picked.GetProperty("nodeId").GetString());
        Assert.Equal("node_01STUDIO", world.Discussion().RunOnNodeId);

        var (_, automatic) = await SetNodeAsync(world, Person(), "null");
        Assert.Equal(JsonValueKind.Null, automatic.GetProperty("nodeId").ValueKind);
        Assert.Null(world.Discussion().RunOnNodeId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"nodeId":7}""")]
    [InlineData("""{"nodeId":""}""")]
    [InlineData("""{"node":"node_01STUDIO"}""")]
    public async Task A_missing_or_malformed_node_is_a_400(string body)
    {
        using var world = new World("session-1");

        var request = JsonSerializer.Deserialize<DiscussionRunOnRequest>(body, Web);
        var result = await DiscussionEndpoints.SetRunOnNodeAsync(world.Discussion(), request, ContextOf(Person()),
            world.Discussions, world.Events);

        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Null(world.Discussion().RunOnNodeId);
    }

    [Fact]
    public async Task Clients_are_told_once_when_the_node_really_changes()
    {
        using var world = new World("session-1");

        await SetNodeAsync(world, Person(), "\"node_01STUDIO\"");
        await SetNodeAsync(world, Person(), "\"node_01STUDIO\"");
        await SetNodeAsync(world, Person(), "null");

        Assert.Equal(2, world.Published.Count(e => e.Name == "discussion.changed"));
    }

    [Fact]
    public async Task Someone_who_cannot_read_the_discussion_cannot_move_it()
    {
        using var world = new World("session-1");
        world.Store.CreateDiscussion("Laurent's", AgentOf(world), new System.Text.Json.Nodes.JsonObject
        {
            ["discussion_id"] = "laurent01", ["app"] = "nova", ["type"] = "chat", ["owner_id"] = "laurent",
        });
        var someoneElses = (await world.Discussions.GetAsync("laurent01"))!;

        var (status, _) = await SetNodeAsync(world, Person(sub: "someone-else"), "\"node_01STUDIO\"", someoneElses);

        Assert.Equal(403, status);
        Assert.Null((await world.Discussions.GetAsync("laurent01"))!.RunOnNodeId);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<(int Status, JsonElement Body)> SetNodeAsync(World world, ClaimsPrincipal user, string value,
        DiscussionRead? discussion = null)
    {
        var request = JsonSerializer.Deserialize<DiscussionRunOnRequest>($$"""{"nodeId":{{value}}}""", Web);
        var result = await DiscussionEndpoints.SetRunOnNodeAsync(discussion ?? world.Discussion(), request,
            ContextOf(user), world.Discussions, world.Events);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        return (status, result is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v) : default);
    }
}
