using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Message Info shows the context the agent read ahead of a user message. Before this, the pipeline
// built that metadata but stored none of it, the context text lived only in the discussion's
// overwritten last_context_json, and neither history path returned any metadata at all.
public sealed class MessageContextPersistenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    // ── Persistence ────────────────────────────────────────────────

    [Fact]
    public async Task A_visible_user_message_stores_exactly_what_the_agent_read()
    {
        using var w = new ReactionContextTests.World();

        var outcome = await w.SendAsync("export the atlas");

        var sent = w.Gateway.Sent.Single();
        var record = UserRecords(w).Single();
        var context = Assert.IsType<JsonObject>(record.Metadata[NovaMessageContext.RecordKey]);
        var block = context[NovaMessageContext.BlockKey]!.GetValue<string>();
        Assert.Equal(sent.Content, block + "export the atlas");
        Assert.StartsWith("<nova-context ", block);
        Assert.Contains($" message=\"{outcome.MessageUid}\"", block);
        Assert.Equal("export the atlas", record.Content);                  // the bubble text is unchanged
        Assert.Equal(outcome.MessageUid, record.Metadata["uid"]!.GetValue<string>());

        // The fields BuildMetadata always computed, now kept with the message.
        Assert.Contains($" device=\"{context["device"]!.GetValue<string>()}\" ", block);   // the label the tag carries
        Assert.Equal("phone", context["deviceType"]!.GetValue<string>());
        Assert.Equal("text", context["input"]!.GetValue<string>());
        Assert.Equal("disc1234", context["discussion"]!.GetValue<string>());
        Assert.NotNull(context["timestamp"]);
        Assert.NotNull(context["activeDiscussionCount"]);
    }

    [Fact]
    public async Task The_prior_message_block_is_part_of_the_stored_context()
    {
        using var w = new ReactionContextTests.World(injectedContext: "Shall I book the train?");

        await w.SendAsync("yes please");

        var block = Block(UserRecords(w).Single());
        Assert.Contains("\n<nova-prior-message role=\"assistant\">\nShall I book the train?\n</nova-prior-message>\n", block);
        Assert.Equal(w.Gateway.Sent.Single().Content, block + "yes please");
    }

    [Fact]
    public async Task Each_turn_keeps_its_own_context_full_then_delta()
    {
        using var w = new ReactionContextTests.World();

        await w.SendAsync("first");
        await w.SendAsync("second");

        var records = UserRecords(w);
        Assert.Equal(2, records.Count);
        Assert.Equal(w.Gateway.Sent[0].Content, Block(records[0]) + "first");
        Assert.Equal(w.Gateway.Sent[1].Content, Block(records[1]) + "second");
        Assert.Contains("Recall any discussion", Block(records[0]));
        Assert.DoesNotContain("Recall any discussion", Block(records[1]));
    }

    [Fact]
    public async Task An_internal_turn_still_persists_no_record()
    {
        using var w = new ReactionContextTests.World();

        await w.Pipeline.SendInternalAsync(w.Discussion(), "owner-1", "bootstrap instruction", "setup-1", "uid-setup");

        Assert.Single(w.Gateway.Sent);
        Assert.Empty(UserRecords(w));
    }

    // ── History ────────────────────────────────────────────────────

    [Fact]
    public async Task The_history_page_returns_the_context_by_the_uid_the_transcript_keeps()
    {
        using var w = new ReactionContextTests.World();
        var outcome = await w.SendAsync("export the atlas");

        var contexts = NovaMessageContext.ForUserMessages(w.Stream.Messages());

        var context = Assert.Single(contexts);
        Assert.Equal(outcome.MessageUid, context.MessageUid);
        Assert.Equal(w.Gateway.Sent.Single().Content, context.Metadata[NovaMessageContext.BlockKey]!.GetValue<string>() + "export the atlas");
    }

    [Fact]
    public void A_canonical_user_message_keeps_its_context_though_its_overlay_is_suppressed()
    {
        var records = new List<DiscussionMessage>
        {
            User(1, "u1", "user-message", WithContext("one")),
            User(2, "u2", "queued-user-message", WithContext("queued")),
            User(3, "u2", "user-message", WithContext("delivered")),        // the same turn, admitted later
            User(4, "u3", "user-message", null),                            // older than this change
            Record(5, "assistant", "nova-message", "n1", WithContext("not a user message")),
            Record(6, "system", "event:automation", "e1", WithContext("not a user message")),
            User(7, "", "user-message", WithContext("no uid")),
        };

        // u1 and u2 are in the canonical transcript, so neither is an overlay any more.
        var overlays = DiscussionEndpoints.ProjectHistoryOverlays(records, new HashSet<string> { "u1", "u2" },
            new HashSet<string>(), sessionBacked: true, allowUserBridges: true);
        var contexts = NovaMessageContext.ForUserMessages(records);

        Assert.DoesNotContain(overlays, overlay => overlay.MessageUid is "u1" or "u2");
        Assert.Equal(["u1", "u2"], contexts.Select(c => c.MessageUid));
        Assert.Equal("one", Block(contexts[0].Metadata));
        Assert.Equal("delivered", Block(contexts[1].Metadata));
    }

    [Fact]
    public void Message_contexts_serialize_as_the_client_reads_them()
    {
        var contexts = NovaMessageContext.ForUserMessages([User(1, "u1", "user-message", WithContext("<nova-context>…</nova-context>"))]);

        var json = JsonNode.Parse(JsonSerializer.Serialize(new { messageContexts = contexts },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        var entry = json["messageContexts"]![0]!;
        Assert.Equal("u1", entry["messageUid"]!.GetValue<string>());
        Assert.Equal("<nova-context>…</nova-context>", entry["metadata"]!["novaContextBlock"]!.GetValue<string>());
        Assert.Equal("Desktop", entry["metadata"]!["device"]!.GetValue<string>());
    }

    [Fact]
    public void The_legacy_history_row_reads_a_copy_and_null_without_a_context()
    {
        var record = User(1, "u1", "user-message", WithContext("text"));

        var read = NovaMessageContext.Read(record)!;
        read[NovaMessageContext.BlockKey] = "changed";

        Assert.Equal("text", Block((JsonObject)record.Metadata[NovaMessageContext.RecordKey]!));
        Assert.Null(NovaMessageContext.Read(User(2, "u2", "user-message", null)));
        Assert.Null(NovaMessageContext.Read(null));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static List<DiscussionMessage> UserRecords(ReactionContextTests.World w)
        => w.Stream.Messages().Where(m => m.Role == "user").ToList();

    private static string Block(DiscussionMessage record)
        => Block((JsonObject)record.Metadata[NovaMessageContext.RecordKey]!);

    private static string Block(JsonObject context) => context[NovaMessageContext.BlockKey]!.GetValue<string>();

    private static JsonObject WithContext(string block)
        => NovaMessageContext.ToRecord(new Dictionary<string, object?> { ["device"] = "Desktop", ["input"] = "text" }, block);

    private static DiscussionMessage User(long id, string uid, string source, JsonObject? context)
        => Record(id, "user", source, uid, context);

    private static DiscussionMessage Record(long id, string role, string source, string uid, JsonObject? context)
    {
        var metadata = new JsonObject { ["source"] = source, ["uid"] = uid };
        if (context is not null) metadata[NovaMessageContext.RecordKey] = context;
        return new(id, Guid.Empty, role, "text", metadata, T0.AddSeconds(id));
    }
}
