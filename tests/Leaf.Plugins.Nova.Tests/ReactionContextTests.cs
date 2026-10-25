using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// ab7a1a07: reactions reach the agent as lines it can read in context (who, which emoji, added
/// or removed, the agent's latest or an earlier message and whose, a 280-character excerpt), in
/// full and delta context, once; they persist into the export net of removals; and a person's
/// reaction steers a running turn, starts one debounced turn on the agent's latest message, or
/// waits for the next context. Emoji carry no meaning anywhere.
/// </summary>
public sealed class ReactionContextTests
{
    private const string Agent = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private static readonly DateTime T0 = new(2026, 10, 25, 14, 0, 0, DateTimeKind.Utc);

    // ── What a reaction points at ──────────────────────────────────

    [Fact]
    public void SessionTranscriptPrefersFinalAnswerOverEarlierTurnRecords()
    {
        var transcript = ReactionTranscript.Build([],
        [
            Text("assistant", "I am checking that now", "turn-1", T0, "commentary"),
            new SessionMessage { Role = "assistant", EventType = "tool_result", Content = "internal tool output",
                MessageUid = "turn-1", Timestamp = T0.AddMilliseconds(1) },
            Text("assistant", "The verified final answer", "turn-1", T0.AddMilliseconds(2), "final_answer"),
        ], Agent);

        Assert.Equal("The verified final answer", transcript.Find("turn-1")!.Content);
        Assert.Equal("turn-1", transcript.LatestAgentUid);
    }

    [Fact]
    public void DirectDiscussionMessagePreviewKeepsPrecedence()
    {
        var transcript = ReactionTranscript.Build(
            [Stream(1, "user", "Persisted discussion message", "shared")],
            [Text("user", "Transcript fallback must not replace it", "shared", T0)], Agent);

        Assert.Equal("Persisted discussion message", transcript.Find("shared")!.Content);
    }

    [Fact]
    public void Latest_agent_message_skips_a_silent_settle_and_counts_direct_nova_messages()
    {
        var transcript = ReactionTranscript.Build(
            [Stream(1, "assistant", "Proactive hello", "direct-1", T0.AddMinutes(1), source: "nova-message")],
            [
                Text("assistant", "First answer", "turn-1", T0),
                Text("user", "<nova-context timestamp=\"x\">\nReactions since your last message:\n</nova-context>", "note-1", T0.AddMinutes(2)),
                Text("assistant", "  " + NovaNoReply.Marker + "\n", "turn-2", T0.AddMinutes(3), "final_answer"),
            ], Agent);

        // The silent turn and the reaction note are not messages; the proactive one is the latest.
        Assert.Null(transcript.Find("turn-2"));
        Assert.Null(transcript.Find("note-1"));
        Assert.Equal("direct-1", transcript.LatestAgentUid);
    }

    [Fact]
    public void Excerpt_is_one_line_bounded_at_280_and_never_splits_a_surrogate_pair()
    {
        var thumbs = char.ConvertFromUtf32(0x1F44D);
        // The pair straddles the cut: 18 characters of words, then 260 x, then the emoji.
        var content = "Line one\r\nline   two\n" + new string('x', 260) + thumbs + "tail beyond the bound";

        var excerpt = ReactionContext.Excerpt(content);

        Assert.DoesNotContain('\n', excerpt);
        Assert.StartsWith("Line one line two x", excerpt);
        Assert.Equal(ReactionContext.ExcerptLength - 1, excerpt.Length);
        Assert.EndsWith("…", excerpt);
        Assert.False(char.IsHighSurrogate(excerpt[^2]));
        Assert.Equal("short and sweet", ReactionContext.Excerpt("  short and sweet "));
    }

    [Fact]
    public void Lines_say_who_what_and_which_message_without_interpreting_the_emoji()
    {
        var transcript = ReactionTranscript.Build(
            [
                Stream(1, "user", "Can you ship it today?", "user-1", T0),
                Stream(2, "assistant", "Another agent chimed in", "other-1", T0.AddMinutes(1), source: "nova-message", sender: "agent-2"),
            ],
            [
                Text("assistant", "Earlier answer", "turn-1", T0.AddMinutes(2)),
                Text("assistant", "Shipped & tested", "turn-2", T0.AddMinutes(3)),
            ], Agent);
        string Line(ReactionEntry entry) => ReactionContext.Line(entry, transcript, Agent, "Laurent");

        Assert.Equal("Laurent reacted ❤️ to your latest message: \"Shipped & tested\"",
            Line(Entry(1, "turn-2", "❤️")));
        Assert.Equal("Laurent removed 👍 from an earlier message of yours: \"Earlier answer\"",
            Line(Entry(2, "turn-1", "👍", remove: true)));
        // The UI keys a later segment of one turn as "uid:segment:N".
        Assert.Equal("Laurent reacted 🎉 to your latest message: \"Shipped & tested\"",
            Line(Entry(3, "turn-2:segment:1", "🎉")));
        Assert.Equal("Laurent reacted 😂 to their own message: \"Can you ship it today?\"",
            Line(Entry(4, "user-1", "😂")));
        Assert.Equal("You reacted 👀 to Laurent's message: \"Can you ship it today?\"",
            Line(Entry(5, "user-1", "👀", actorType: "agent", actorId: Agent, actorName: "Nova")));
        Assert.Equal("Laurent reacted 👍 to another agent's message: \"Another agent chimed in\"",
            Line(Entry(6, "other-1", "👍")));
        Assert.Equal("Laurent reacted 👍 to a message that is no longer in this discussion",
            Line(Entry(7, "gone-1", "👍")));
    }

    [Fact]
    public void Net_cancels_an_add_and_remove_pair_and_keeps_a_lone_removal()
    {
        var net = ReactionContext.Net(
        [
            Entry(1, "turn-1", "❤️"),
            Entry(2, "turn-1", "❤️", remove: true),
            Entry(3, "turn-1", "👍", remove: true),
            Entry(4, "turn-2", "🔥"),
            Entry(5, "turn-2", "🔥", remove: true),
            Entry(6, "turn-2", "🔥"),
        ]);

        Assert.Collection(net,
            removal => Assert.Equal((3L, true), (removal.Id, removal.Remove)),
            readded => Assert.Equal((6L, false), (readded.Id, readded.Remove)));
    }

    // ── In the context ─────────────────────────────────────────────

    [Fact]
    public async Task ReactionContextResolvesCanonicalComputeTranscriptMessage()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Canonical assistant answer", "assistant-turn", T0));
        w.Stream.React("assistant-turn", "🔥");

        var notes = await w.Pipeline.GetReactionNotesAsync(w.Discussion(), CancellationToken.None);

        Assert.Equal("Laurent reacted 🔥 to your latest message: \"Canonical assistant answer\"", Assert.Single(notes.Lines));
        Assert.Contains("/ai-session/sessions/session-1?tail=500", w.Gateway.Reads);
    }

    [Fact]
    public async Task Delta_context_carries_each_reaction_change_once_including_removals()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Here is the plan", "turn-1", T0));
        await w.SendAsync("hello");                       // the first message: full context
        w.Stream.React("turn-1", "❤️");
        w.Stream.React("turn-1", "👍");
        w.Stream.React("turn-1", "👍", remove: true);     // nets out
        w.Stream.React("old-turn", "🙂", remove: true);   // a removal of something added earlier

        await w.SendAsync("second");
        await w.SendAsync("third");

        var second = w.Gateway.Sent[1].Content;
        Assert.Contains("Changes since last message:", second);
        Assert.Contains("- Laurent reacted ❤️ to your latest message: \"Here is the plan\"", second);
        Assert.Contains("- Laurent removed 🙂 from a message that is no longer in this discussion", second);
        Assert.DoesNotContain("👍", second);
        Assert.DoesNotContain("reacted", w.Gateway.Sent[2].Content);
    }

    [Fact]
    public async Task Full_context_renders_reactions_made_before_the_first_message()
    {
        using var w = new World();
        w.Stream.Post("assistant", "Good morning! Coffee?", "proactive-1", "nova-message");
        w.Stream.React("proactive-1", "☕");

        await w.SendAsync("yes please");

        var full = w.Gateway.Sent.Single().Content;
        Assert.Contains("Reactions since your last message:\n- Laurent reacted ☕ to your latest message: \"Good morning! Coffee?\"", full);
        Assert.Contains("React to a message", full);
    }

    [Fact]
    public async Task Context_tag_names_the_incoming_message_uid_the_transcript_keeps()
    {
        using var w = new World();

        var outcome = await w.SendAsync("react to this");

        var sent = w.Gateway.Sent.Single();
        Assert.Matches("^[0-9a-f]{32}$", outcome.MessageUid!);
        Assert.Equal(outcome.MessageUid, sent.MessageUid);
        Assert.Contains($" message=\"{outcome.MessageUid}\" ", sent.Content[..sent.Content.IndexOf('\n')]);
    }

    // ── In the export ──────────────────────────────────────────────

    [Fact]
    public async Task Export_attaches_standing_reactions_to_their_message_net_of_removals()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("user", "Can we ship?", "user-1", T0));
        w.Gateway.Transcript.Add(Text("assistant", "Checking", "turn-1", T0.AddSeconds(1), "commentary"));
        w.Gateway.Transcript.Add(Text("assistant", "Shipped.", "turn-1", T0.AddSeconds(2), "final_answer"));
        w.Gateway.Transcript.Add(Text("assistant", NovaNoReply.Marker, "turn-2", T0.AddSeconds(3), "final_answer"));
        w.Stream.React("turn-1", "❤️", at: T0.AddMinutes(5));
        w.Stream.React("turn-1", "👍", at: T0.AddMinutes(6));
        w.Stream.React("turn-1", "👍", remove: true, at: T0.AddMinutes(7));
        w.Stream.React("user-1", "👀", actorType: "agent", actorId: Agent, actorName: "Nova", at: T0.AddMinutes(8));

        var export = await new ConversationExporter(new RedComputeClient(w.Gateway), w.Stream)
            .ExportSingleAsync(w.Discussion());
        export = export.ReplaceLineEndings("\n");

        Assert.Contains("Can we ship?\nReactions: 👀 Nova (14:08)\n", export);
        // After the turn's last entry (its final answer), not after the commentary.
        Assert.Contains("Checking\n\n", export);
        Assert.Contains("Shipped.\nReactions: ❤️ Laurent (14:05)\n", export);
        Assert.DoesNotContain("👍", export);
        Assert.DoesNotContain(NovaNoReply.Marker, export);
    }

    // ── Who reacts ─────────────────────────────────────────────────

    [Fact]
    public void Reaction_actor_comes_from_the_signed_identity_and_stays_compatible()
    {
        var agent = Principal(actorKind: "agent", actorId: "nova", entityId: Agent, actorName: "Nova");
        var automation = Principal(actorKind: "automation", actorId: "automation-7", actorName: "Morning brief");
        var browser = Principal(actorKind: "app", actorId: "nova", browser: true, name: "Laurent");
        var person = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner-1"), new Claim("name", "Laurent")], "Cookie"));

        Assert.Equal(("agent", Agent, "Nova"), DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍" }, agent, "owner-1"));
        // A signed Agent is itself whatever the body claims.
        Assert.Equal(("agent", Agent, "Nova"),
            DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍", AgentId = "someone-else", AgentName = "X" }, agent, "owner-1"));
        // Older recipes name the Agent in the body.
        Assert.Equal(("agent", "legacy-agent", "Nova"),
            DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍", AgentId = "legacy-agent", AgentName = "Nova" }, person, "owner-1"));
        Assert.Equal(("user", "owner-1", "Laurent"), DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍" }, person, "owner-1"));
        Assert.Equal(("user", "owner-1", "Laurent"), DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍" }, browser, "owner-1"));
        Assert.Equal(("automation", "automation-7", "Morning brief"),
            DiscussionEndpoints.ResolveReactionActor(new() { Emoji = "👍" }, automation, "owner-1"));
    }

    // ── When it costs a turn ───────────────────────────────────────

    [Fact]
    public async Task Mid_turn_reaction_steers_one_note_into_the_running_turn()
    {
        using var w = new World();
        w.Gateway.Status = "Active";
        w.Gateway.Transcript.Add(Text("assistant", "Earlier answer", "turn-1", T0));

        var outcome = await w.ReactAsync("turn-1", "👍");

        Assert.Equal(ReactionTriggerOutcome.Steered, outcome);
        var note = Assert.Single(w.Gateway.Sent);
        Assert.Equal("steer-current", note.Delivery);
        AssertHiddenNote(note);
        Assert.Contains("needs no reply of its own", note.Content);
        Assert.DoesNotContain(NovaNoReply.Marker, note.Content);
        // Carried once: the next context does not repeat it.
        w.Gateway.Status = "Idle";
        await w.SendAsync("next");
        Assert.DoesNotContain("reacted", w.Gateway.Sent[1].Content);
    }

    [Fact]
    public async Task Idle_reaction_on_an_older_message_starts_nothing_and_waits_for_the_next_context()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Earlier answer", "turn-1", T0));
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0.AddMinutes(1)));

        Assert.Equal(ReactionTriggerOutcome.Folded, await w.ReactAsync("turn-1", "👍"));
        Assert.Null(w.Triggers.PendingFire("disc1234", "owner-1"));
        Assert.Empty(w.Gateway.Sent);

        await w.SendAsync("next");
        Assert.Contains("Laurent reacted 👍 to an earlier message of yours: \"Earlier answer\"", w.Gateway.Sent.Single().Content);
    }

    [Fact]
    public async Task Idle_reactions_on_the_latest_message_start_exactly_one_turn_after_the_debounce()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0));

        Assert.Equal(ReactionTriggerOutcome.Debounced, await w.ReactAsync("turn-2", "❤️"));
        Assert.Equal(ReactionTriggerOutcome.Debounced, await w.ReactAsync("turn-2", "🔥"));
        Assert.Empty(w.Gateway.Sent);   // nothing before the window closes

        await w.CloseWindowAsync();

        var turn = Assert.Single(w.Gateway.Sent);
        Assert.Null(turn.Delivery);
        AssertHiddenNote(turn);
        Assert.Contains("- Laurent reacted ❤️ to your latest message: \"Latest answer\"", turn.Content);
        Assert.Contains("- Laurent reacted 🔥 to your latest message: \"Latest answer\"", turn.Content);
        Assert.Contains("A reaction-only or silent reply (exactly " + NovaNoReply.Marker + ")", turn.Content);
        Assert.Equal(TimeSpan.FromSeconds(8), w.Triggers.Debounce);
    }

    [Fact]
    public async Task Add_then_remove_inside_the_window_starts_nothing()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0));

        await w.ReactAsync("turn-2", "❤️");
        await w.ReactAsync("turn-2", "❤️", remove: true);
        await w.CloseWindowAsync();

        Assert.Empty(w.Gateway.Sent);
        await w.SendAsync("next");
        Assert.DoesNotContain("❤️", w.Gateway.Sent.Single().Content);
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("automation", false)]
    [InlineData("user", false)]   // a person's identity, but not a person acting (a worker, a heartbeat)
    public async Task Agent_and_automation_reactions_never_trigger(string actorType, bool humanInitiated)
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0));

        foreach (var status in new[] { "Idle", "Active" })
        {
            w.Gateway.Status = status;
            Assert.Equal(ReactionTriggerOutcome.Ignored,
                await w.ReactAsync("turn-2", "👍", actorType: actorType, humanInitiated: humanInitiated));
        }
        Assert.Null(w.Triggers.PendingFire("disc1234", "owner-1"));
        Assert.Empty(w.Gateway.Sent);
    }

    [Fact]
    public async Task A_message_sent_during_the_window_carries_the_reactions_and_cancels_the_turn()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0));

        await w.ReactAsync("turn-2", "❤️");
        await w.SendAsync("also, one more thing");
        await w.CloseWindowAsync();

        var sent = Assert.Single(w.Gateway.Sent);
        Assert.Contains("Laurent reacted ❤️ to your latest message", sent.Content);
    }

    [Fact]
    public async Task Reaction_turns_are_internal_and_never_a_person_talking()
    {
        using var w = new World();
        w.Gateway.Transcript.Add(Text("assistant", "Latest answer", "turn-2", T0));
        var before = w.Discussion();

        await w.ReactAsync("turn-2", "❤️");
        await w.CloseWindowAsync();

        // No Nova user-message record, no activity bump, no unread change.
        Assert.DoesNotContain(w.Stream.Messages(), message => message.Role == "user");
        var after = w.Discussion();
        Assert.Equal(before.LastActivity, after.LastActivity);
        Assert.Equal(before.ConversationRevision, after.ConversationRevision);
        Assert.NotNull(after.ReactionsNoted);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static void AssertHiddenNote(Gateway.Message note)
    {
        // A pure envelope: Nova's chat view and export drop it, so no bubble ever shows.
        Assert.StartsWith("<nova-context ", note.Content);
        Assert.EndsWith("</nova-context>", note.Content);
        Assert.Equal(note.Content, note.DisplayContent);
        Assert.Equal("", ConversationExporter.StripInjectedTags(note.Content));
        Assert.Contains(" input=\"reaction\" ", note.Content);
        Assert.True(note.Internal);
    }

    private static SessionMessage Text(string role, string content, string uid, DateTime at, string? phase = null)
        => new() { Role = role, EventType = "text", Content = content, MessageUid = uid, Timestamp = at, Phase = phase };

    private static DiscussionMessage Stream(long id, string role, string content, string uid, DateTime? at = null,
        string? source = null, string? sender = null)
        => new(id, Guid.Empty, role, content, new JsonObject
        {
            ["uid"] = uid,
            ["source"] = source ?? (role == "user" ? "user-message" : "nova-message"),
            ["sender_agent_id"] = sender,
        }, at ?? T0);

    private static ReactionEntry Entry(long id, string key, string emoji, bool remove = false,
        string actorType = "user", string actorId = "owner-1", string actorName = "Laurent")
        => new(id, key, emoji, remove, actorType, actorId, actorName, T0);

    private static ClaimsPrincipal Principal(string actorKind, string actorId, string? entityId = null,
        string? actorName = null, bool browser = false, string? name = null)
    {
        var identity = new JsonObject
        {
            ["app"] = new JsonObject { ["id"] = "nova" },
            ["actor"] = new JsonObject { ["kind"] = actorKind, ["id"] = actorId, ["entityId"] = entityId, ["name"] = actorName },
            ["beneficiary"] = new JsonObject { ["kind"] = "user", ["id"] = "owner-1" },
            ["context"] = browser
                ? new JsonArray(new JsonObject { ["kind"] = "browser", ["route"] = "/apps/nova" })
                : new JsonArray(),
        };
        var claims = new List<Claim>
        {
            new("sub", "owner-1"), new("token_use", "execution"), new("execution_identity", identity.ToJsonString()),
        };
        if (name is not null) claims.Add(new("name", name));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private sealed class World : IDisposable
    {
        private readonly string scratch = Path.Combine(Path.GetTempPath(), $"nova-reaction-test-{Guid.NewGuid():N}");
        private readonly AgentDirectory agents;
        private readonly TaskCompletionSource window = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Entities Store { get; } = new();
        public Discussions Stream { get; }
        public Gateway Gateway { get; } = new();
        public MessagePipeline Pipeline { get; }
        public ReactionTriggers Triggers { get; }

        public World()
        {
            Directory.CreateDirectory(scratch);
            Stream = new Discussions(Store.DiscussionId);
            var events = CallbackDeliveryTests.Proxy.Create<IPluginEvents>((method, _) => method == "Subscribe" ? new Noop() : Task.CompletedTask);
            agents = new AgentDirectory(Store, events) { NovaAgentId = Agent };
            var client = new RedComputeClient(Gateway);
            var store = new DiscussionStore(Store, Stream);
            var unread = new ConversationUnread(store, Stream, client);
            var injector = new EventInjector(Stream, events, client, agents, Store, unread);
            var extensions = CallbackDeliveryTests.Proxy.Create<IPluginExtensions>((method, _) => method switch
            {
                "CollectContextAsync" => Task.FromResult<IReadOnlyList<PluginContextFragment>>([]),
                _ => new Noop(),
            });
            Pipeline = new MessagePipeline(store, Stream, Store, null!, client, agents,
                new AgentWorkspaces(agents, new NoWorkspace()), new Scratch(scratch),
                new ExtensionContributions(extensions, new LiveEvents(store, injector, agents)),
                unread, NullLogger<MessagePipeline>.Instance);
            Triggers = new ReactionTriggers(store, Pipeline, client, NullLogger<ReactionTriggers>.Instance)
            {
                Delay = _ => window.Task,
            };
        }

        public DiscussionRead Discussion() => DiscussionStore.Map(Store.Get(Store.DiscussionId))!;

        public Task<SendMessageOutcome> SendAsync(string content)
            => Pipeline.SendAsync(Discussion(), "owner-1", content, null,
                new ResolvedDevice { Name = "Pixel 9", Type = "phone", Platform = "android" }, "text");

        /// <summary>What the reaction endpoint does: record, then hand the change to the policy.</summary>
        public Task<ReactionTriggerOutcome> ReactAsync(string key, string emoji, bool remove = false,
            string actorType = "user", bool humanInitiated = true)
        {
            var entry = Stream.React(key, emoji, remove, actorType,
                actorType == "agent" ? Agent : "owner-1", actorType == "agent" ? "Nova" : "Laurent");
            return Triggers.OnReactionAsync(new ReactionSignal(Discussion(), entry, humanInitiated,
                new ResolvedDevice { Name = "Pixel 9", Type = "phone", Platform = "android" }));
        }

        public async Task CloseWindowAsync()
        {
            var pending = Triggers.PendingFire("disc1234", "owner-1");
            window.TrySetResult();
            if (pending is not null) await pending;
        }

        public void Dispose()
        {
            agents.Dispose();
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    internal sealed class Entities : IEntityStore
    {
        private readonly List<LeafEntity> entities = [];
        public Guid DiscussionId { get; }

        public Entities()
        {
            var agent = New("agent", "nova", new JsonObject(), Guid.Parse(Agent));
            entities.Add(agent);
            entities.Add(New("plugin", NovaAppPlugin.PluginId, new JsonObject()));
            var discussion = New("discussion", DiscussionStore.Slug("disc1234"), new JsonObject
            {
                ["discussion_id"] = "disc1234", ["app"] = "nova", ["type"] = "chat", ["status"] = "idle",
                ["agent"] = Agent, ["owner_id"] = "owner-1", ["session_id"] = "session-1",
                ["last_activity"] = T0.AddDays(-1).ToString("O"), ["message_count"] = 3,
                ["conversation_revision"] = 0, ["read_conversation_revision"] = 0,
                ["last_processed_session_assistant_uid"] = "",
            });
            entities.Add(discussion);
            DiscussionId = discussion.Id;
        }

        public LeafEntity Get(Guid id) { lock (entities) return entities.Single(e => e.Id == id); }

        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
        {
            lock (entities)
                // The Agent is addressed by the discussion's agent id.
                return Task.FromResult(entities.FirstOrDefault(e => e.Id == id));
        }

        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
        { lock (entities) return Task.FromResult(entities.FirstOrDefault(e => e.Slug == slug)); }

        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
        {
            lock (entities)
            {
                IEnumerable<LeafEntity> rows = entities.Where(e => e.TypeSlug == query.TypeSlug);
                foreach (var (key, value) in query.DataEquals ?? new Dictionary<string, object?>())
                    rows = rows.Where(e => JsonNode.DeepEquals(e.Data[key], JsonSerializer.SerializeToNode(value)));
                return Task.FromResult<IReadOnlyList<LeafEntity>>(rows.Skip(query.Offset).Take(query.Limit).ToList());
            }
        }

        public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
        {
            lock (entities)
            {
                var index = entities.FindIndex(e => e.Id == id);
                var data = entities[index].Data.DeepClone().AsObject();
                foreach (var (key, value) in dataPatch) data[key] = value?.DeepClone();
                entities[index] = entities[index] with { Data = data, UpdatedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(entities[index]);
            }
        }

        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        private static LeafEntity New(string type, string slug, JsonObject data, Guid? id = null)
            => new(id ?? Guid.NewGuid(), type, slug, slug, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test");
    }

    /// <summary>The kernel's discussion streams: messages and the add/remove reaction log.</summary>
    internal sealed class Discussions(Guid discussionId) : IDiscussions
    {
        private readonly List<DiscussionMessage> messages = [];
        private readonly List<LeafRecord> reactions = [];

        public DiscussionMessage[] Messages() { lock (messages) return [.. messages]; }

        public void Post(string role, string content, string uid, string source)
        {
            lock (messages)
                messages.Add(new(messages.Count + 1, discussionId, role, content,
                    new JsonObject { ["uid"] = uid, ["source"] = source }, T0.AddMinutes(-30)));
        }

        public ReactionEntry React(string key, string emoji, bool remove = false, string actorType = "user",
            string actorId = "owner-1", string actorName = "Laurent", DateTime? at = null)
        {
            lock (reactions)
            {
                var record = new LeafRecord(reactions.Count + 1, "message-reactions", discussionId, actorId, new JsonObject
                {
                    ["message_key"] = key, ["emoji"] = emoji, ["action"] = remove ? "remove" : "add",
                    ["actor_type"] = actorType, ["actor_id"] = actorId, ["actor_name"] = actorName,
                }, at ?? DateTimeOffset.UtcNow);
                reactions.Add(record);
                return ReactionEntry.Read(record);
            }
        }

        public Task PostAsync(Guid id, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default)
        {
            lock (messages) messages.Add(new(messages.Count + 1, id, role, content, metadata ?? new JsonObject(), DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid id, int limit = 1000, long afterId = 0, CancellationToken ct = default)
        {
            lock (messages)
                return Task.FromResult<IReadOnlyList<DiscussionMessage>>(
                    messages.Where(m => m.DiscussionId == id && m.Id > afterId).Take(limit).ToArray());
        }

        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid id, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default)
        {
            lock (reactions)
                return Task.FromResult<IReadOnlyList<LeafRecord>>(reactions
                    .Where(r => r.EntityId == id && (since is null || r.CreatedAt >= since))
                    .OrderBy(r => r.Id).Take(limit).ToArray());
        }

        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearMessagesAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetReactionAsync(Guid id, ReactionChange change, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable Subscribe(Guid id, Func<DiscussionMessage, Task> onMessage) => new Noop();
    }

    /// <summary>RedCompute's session API: a status, a transcript, and every admitted input.</summary>
    internal sealed class Gateway : IComputeGateway
    {
        public string Status { get; set; } = "Idle";
        public List<SessionMessage> Transcript { get; } = [];
        public List<Message> Sent { get; } = [];
        public List<string> Reads { get; } = [];

        public sealed record Message(string Content, string? DisplayContent, string? Delivery, string? MessageUid,
            string? Key, bool Internal);

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            var path = request.RequestUri!.OriginalString;
            if (request.Method == HttpMethod.Get && path.StartsWith("/ai-session/sessions/session-1", StringComparison.Ordinal))
            {
                lock (Sent) Reads.Add(path);
                var body = new JsonObject
                {
                    ["session"] = new JsonObject { ["id"] = "session-1", ["status"] = Status },
                    ["messages"] = JsonSerializer.SerializeToNode(Transcript.Select(m => new
                    {
                        role = m.Role, eventType = m.EventType, content = m.Content, messageUid = m.MessageUid,
                        phase = m.Phase, timestamp = m.Timestamp,
                    }), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                };
                return Json(body.ToJsonString());
            }
            if (request.Method == HttpMethod.Post && path == "/ai-session/sessions/session-1/message")
            {
                Assert.NotNull(provenance);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var isInternal = root.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("internalMessage", out var flag) && flag.GetBoolean();
                var key = request.Headers.TryGetValues("X-Idempotency-Key", out var keys) ? keys.Single() : null;
                var uid = Str("messageUid") ?? Guid.NewGuid().ToString("N");
                lock (Sent) Sent.Add(new(Str("content")!, Str("displayContent"), Str("delivery"), uid, key, isInternal));
                return Json($$"""{"disposition":"delivered","messageUid":"{{uid}}"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class NoWorkspace : IAgentWorkspacePathResolver
    {
        public Task<string?> ResolveAsync(Guid agentId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class Scratch(string path) : IAgentScratchSpace
    {
        public AgentScratchAllocation PrepareExecution(string owner, string executionKey) => new(path, new Dictionary<string, string>());
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
}
