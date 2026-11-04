using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// The discussion's half of a rollback, with RedCompute and the kernel faked. The fake reads the way
/// the kernel does (RedLeaf's RecordRollbackTests prove that): a marked record leaves every read.
/// </summary>
public sealed class DiscussionRollbackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_rollback_marks_what_it_removed_and_keeps_the_rest()
    {
        var f = new Fixture();
        f.Post(1, "user", "kept question", "u1", T0);
        f.Post(2, "assistant", "kept answer", "a1", T0.AddMinutes(1), "nova-message");
        f.Post(3, "user", "the refused message", "u2", T0.AddMinutes(2));
        f.Post(4, "assistant", "a marker with no uid of its own", null, T0.AddMinutes(3), "event:system");
        f.Post(5, "assistant", "named by uid, written before the target", "a2", T0.AddMinutes(1));
        f.Answer(removed: ["u2", "a2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(200, status);
        Assert.Equal("The refused message", body.GetProperty("restoredText").GetString());
        Assert.False(body.GetProperty("filesChanged").GetBoolean());
        Assert.Equal([1L, 2L], f.Discussions.Visible.Select(message => message.Id));
        Assert.Equal([3L, 4L, 5L], f.Discussions.MarkedIds.Order());
        Assert.Equal(["rollback-1"], f.Discussions.MarkedWith.Distinct());
        Assert.Equal("discussion.rolled_back", f.Events.Published.Single().Type);
        Assert.Equal(f.Id, f.Events.Published.Single().Payload["discussionId"]!.GetValue<string>());
        Assert.Equal("""{"messageUid":"u2"}""", f.Compute.RollbackBodies.Single());
        Assert.EndsWith($"/ai-session/sessions/{Fixture.Session}/rollback", f.Compute.RollbackUrls.Single());
    }

    [Fact]
    public async Task The_LIVE_notes_quoting_what_was_removed_go_with_it()
    {
        var f = new Fixture(type: "live");
        var rollback = new DiscussionRollback("rollback-1", ["u2"], T0.AddMinutes(2), T0.AddMinutes(10));
        f.PostNote(1, "rewound", "user-message", "u1", T0);                 // quotes a kept message
        f.PostNote(2, "rewound", "user-message", null, T0);                 // an older note, before the rewound message
        f.PostNote(3, "rewound", "user-message", "u2", T0.AddMinutes(1));   // named, though written before the window
        f.PostNote(4, "rewound", "user-message", null, T0.AddMinutes(3));   // an older note, in the window
        f.PostNote(5, "rewound", "nova-message", "a2", T0.AddMinutes(4));   // a reply in the window
        f.PostNote(6, "rewound", "archived", null, T0.AddMinutes(5));       // quotes nothing
        f.PostNote(7, "elsewhere", "user-message", "u2", T0.AddMinutes(3)); // another discussion's conversation
        f.PostNote(8, "rewound", "user-message", "u9", T0.AddMinutes(11));  // written after the rewind
        f.Post(9, "system", "Now playing", "e9", T0.AddMinutes(3), "event:spotify");
        f.Discussions.All.Add(new DiscussionMessage(10, f.Entity.Id, "system", "broken", new JsonObject
        {
            ["source"] = "event:discussion",
            ["parts_json"] = "{not json",
        }, T0.AddMinutes(3)));

        Assert.Equal(3, await f.Store.ForgetQuotesAsync(f.Entity.Id, "rewound", rollback));

        Assert.Equal([3L, 4L, 5L], f.Discussions.MarkedIds.Order());
        Assert.Equal(["rollback-1"], f.Discussions.MarkedWith);
        Assert.Equal(0, await f.Store.ForgetQuotesAsync(f.Entity.Id, "rewound", rollback));
    }

    [Fact]
    public async Task A_rewind_forgets_its_quotes_and_a_failure_is_finished_by_a_repeat()
    {
        var f = new Fixture();
        f.Post(1, "user", "the refused message", "u2", T0.AddMinutes(2));
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));
        f.FailForgets = 1;

        Assert.Equal(503, (await f.PostAsync("u2")).Status);
        Assert.Empty(f.Forgotten);
        Assert.Empty(f.Events.Published);

        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10), already: true);
        Assert.Equal(200, (await f.PostAsync("u2")).Status);
        Assert.Equal([(f.Id, "rollback-1")], f.Forgotten);
    }

    [Fact]
    public async Task A_refused_rewind_forgets_nothing()
    {
        var f = new Fixture();
        f.Compute.RollbackStatus = HttpStatusCode.Conflict;
        f.Compute.RollbackAnswer = """{"error":"rollback_unsupported"}""";

        Assert.Equal(409, (await f.PostAsync("u2")).Status);
        Assert.Empty(f.Forgotten);
    }

    [Fact]
    public async Task A_late_repeat_keeps_what_was_written_after_the_rollback()
    {
        var f = new Fixture();
        f.Post(1, "user", "kept", "u1", T0);
        f.Post(2, "user", "removed", "u2", T0.AddMinutes(2));
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));
        Assert.Equal(200, (await f.PostAsync("u2")).Status);

        // The person carried on. A retry of the same request then arrives.
        f.Post(3, "user", "the next message", "u3", T0.AddMinutes(11));
        f.Post(4, "assistant", "a marker after it", null, T0.AddMinutes(12), "event:system");
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10), already: true);
        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("alreadyRolledBack").GetBoolean());
        Assert.Equal([1L, 3L, 4L], f.Discussions.Visible.Select(message => message.Id));
    }

    [Fact]
    public async Task A_failure_after_RedCompute_applied_says_so_and_a_repeat_finishes_it()
    {
        var f = new Fixture();
        f.Post(1, "user", "kept", "u1", T0);
        f.Post(2, "user", "removed", "u2", T0.AddMinutes(2));
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));
        f.Discussions.FailMarks = 1;

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(503, status);
        Assert.Equal("rollback_incomplete", body.GetProperty("error").GetString());
        Assert.Empty(f.Events.Published);
        Assert.Equal(2, f.Discussions.Visible.Count());

        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10), already: true);
        Assert.Equal(200, (await f.PostAsync("u2")).Status);
        Assert.Equal([1L], f.Discussions.Visible.Select(message => message.Id));
    }

    [Theory]
    [InlineData(409, "rollback_not_idle")]
    [InlineData(409, "rollback_unsupported")]
    [InlineData(404, "target_not_found")]
    [InlineData(503, "provider_unavailable")]
    public async Task RedCompute_refusing_passes_through_unchanged_and_touches_nothing(int code, string error)
    {
        var f = new Fixture();
        f.Post(1, "user", "kept", "u1", T0);
        var sent = $$"""{"error":"{{error}}","message":"in its own words","reasons":["turn_active"]}""";
        f.Compute.RollbackStatus = (HttpStatusCode)code;
        f.Compute.RollbackAnswer = sent;
        var before = f.Entity.Data.ToJsonString();

        var result = await f.CallAsync("u1");

        var content = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal(code, content.StatusCode);
        Assert.Equal(sent, content.ResponseContent);
        Assert.Empty(f.Discussions.MarkedIds);
        Assert.Empty(f.Events.Published);
        Assert.Equal(before, f.Entity.Data.ToJsonString());
    }

    [Fact]
    public async Task RedCompute_unreachable_is_a_502_and_touches_nothing()
    {
        var f = new Fixture();
        f.Compute.Throw = true;

        var (status, body) = await f.PostAsync("u1");

        Assert.Equal(502, status);
        Assert.Equal("redcompute_unavailable", body.GetProperty("error").GetString());
        Assert.Empty(f.Events.Published);
    }

    [Fact]
    public async Task No_session_means_nothing_to_rewind()
    {
        var f = new Fixture(sessionId: null);

        var (status, body) = await f.PostAsync("u1");

        Assert.Equal(409, status);
        Assert.Equal("rollback_unsupported", body.GetProperty("error").GetString());
        Assert.Empty(f.Compute.RollbackUrls);
    }

    public static TheoryData<string> People => new() { "browser", "another app's browser", "loopback browser" };

    public static TheoryData<string> NotPeople => new()
    {
        "agent", "agent in this discussion", "automation", "agent with a browser context", "derived child of a browser token",
        "browser for someone else", "local subject claiming a user", "system browser token for a named subject", "no browser", "plain login", "service", "system", "loopback without a token",
    };

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_person_at_a_browser_can_rewind_and_their_token_carries_it(string caller)
    {
        var f = new Fixture();
        f.Answer(["u2"], T0, T0.AddMinutes(1));

        var (status, _) = await f.PostAsync("u2", caller: caller);

        Assert.Equal(200, status);
        Assert.Single(f.Compute.RollbackUrls);
        // The gateway derives the identity from the token. Provenance would name an Agent instead.
        Assert.Null(Assert.Single(f.Compute.RollbackProvenance));
    }

    [Theory]
    [MemberData(nameof(NotPeople))]
    public async Task Anything_else_with_read_access_is_refused_before_RedCompute(string caller)
    {
        // The services own what they read here, so only the person check can refuse them.
        var f = new Fixture(owner: caller switch { "service" => "service:nova", "system" => "system", _ => "me" });
        f.Answer(["u2"], T0, T0.AddMinutes(1));

        var (status, body) = await f.PostAsync("u2", caller: caller);

        Assert.Equal(403, status);
        Assert.Equal("rollback_requires_person", body.GetProperty("error").GetString());
        Assert.Empty(f.Compute.RollbackUrls);
    }

    [Fact]
    public async Task The_owner_at_Nova_can_rewind_a_confidential_discussion()
    {
        var f = new Fixture(confidential: true);
        f.Answer(["u2"], T0, T0.AddMinutes(1));

        var (status, _) = await f.PostAsync("u2");

        Assert.Equal(200, status);
        Assert.Single(f.Compute.RollbackUrls);
    }

    [Fact]
    public async Task An_Agent_that_owns_a_confidential_discussion_still_cannot_rewind_it()
    {
        var f = new Fixture(confidential: true);
        f.Answer(["u2"], T0, T0.AddMinutes(1));

        var (status, body) = await f.PostAsync("u2", caller: "owning agent");

        Assert.Equal(403, status);
        Assert.Equal("rollback_requires_person", body.GetProperty("error").GetString());
        Assert.Empty(f.Compute.RollbackUrls);
    }

    [Fact]
    public async Task Refusals_of_its_own_never_reach_RedCompute()
    {
        Assert.Equal(404, (await new Fixture().PostAsync("u1", id: "missing")).Status);
        Assert.Equal(403, (await new Fixture(owner: "someone-else").PostAsync("u1")).Status);
        Assert.Equal(404, (await new Fixture(owner: "someone-else", confidential: true).PostAsync("u1")).Status);
        Assert.Equal(409, (await new Fixture(status: "archived").PostAsync("u1")).Status);
        Assert.Equal(409, (await new Fixture(ownerPlugin: Guid.NewGuid()).PostAsync("u1")).Status);
        Assert.Equal(400, (await new Fixture().PostAsync("  ")).Status);
    }

    [Fact]
    public async Task Nothing_is_unread_because_of_a_rewind_and_the_next_reply_is_new()
    {
        var f = new Fixture();
        f.Entity.Data["conversation_revision"] = 7;
        f.Entity.Data["read_conversation_revision"] = 5;
        f.Entity.Data["last_processed_session_assistant_uid"] = "a2-removed";
        f.Entity.Data["last_context_json"] = "{}";
        f.Compute.Transcript = [Assistant("a1-kept", T0)];
        f.Answer(removed: ["u2", "a2-removed"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));

        var (status, body) = await f.PostAsync("u2");

        Assert.Equal(200, status);
        var read = (await f.Store.GetAsync(f.Id))!;
        Assert.Equal(7, read.ConversationRevision);
        Assert.Equal(7, read.ReadConversationRevision);
        Assert.Equal("a1-kept", read.LastProcessedSessionAssistantUid);
        Assert.Null(read.LastContextJson);
        Assert.Equal(f.Id, body.GetProperty("discussion").GetProperty("id").GetString());

        // Reconciling the kept transcript is not news.
        var settled = await f.Store.ReconcileConversationAsync(read.EntityId, "a1-kept");
        Assert.Equal(7, settled!.ConversationRevision);
        Assert.Equal(7, settled.ReadConversationRevision);

        // The next reply is.
        var next = await f.Store.ReconcileConversationAsync(read.EntityId, "a3-new");
        Assert.Equal(8, next!.ConversationRevision);
        Assert.Equal(7, next.ReadConversationRevision);
    }

    [Fact]
    public async Task Rewinding_past_every_reply_leaves_no_assistant_to_remember()
    {
        var f = new Fixture();
        f.Entity.Data["last_processed_session_assistant_uid"] = "a1-removed";
        f.Compute.Transcript = [];
        f.Answer(removed: ["u1", "a1-removed"], from: T0, at: T0.AddMinutes(10));

        Assert.Equal(200, (await f.PostAsync("u1")).Status);

        Assert.Equal("", (await f.Store.GetAsync(f.Id))!.LastProcessedSessionAssistantUid);
    }

    [Fact]
    public async Task A_direct_message_that_stays_is_not_taken_for_a_session_reply()
    {
        var f = new Fixture();
        f.Post(1, "assistant", "said directly", "direct", T0, "nova-message");
        f.Compute.Transcript = [Assistant("session-reply", T0.AddMinutes(-1)), Assistant("direct", T0)];
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));

        Assert.Equal(200, (await f.PostAsync("u2")).Status);

        Assert.Equal("session-reply", (await f.Store.GetAsync(f.Id))!.LastProcessedSessionAssistantUid);
    }

    [Fact]
    public async Task An_unreadable_transcript_leaves_the_last_processed_turn_alone()
    {
        var f = new Fixture();
        f.Entity.Data["last_processed_session_assistant_uid"] = "as-it-was";
        f.Compute.Transcript = null;
        f.Answer(removed: ["u2"], from: T0, at: T0.AddMinutes(10));

        Assert.Equal(200, (await f.PostAsync("u2")).Status);

        Assert.Equal("as-it-was", (await f.Store.GetAsync(f.Id))!.LastProcessedSessionAssistantUid);
    }

    [Fact]
    public async Task Export_and_LIVE_read_only_what_is_kept()
    {
        var f = new Fixture(type: "live");
        f.Post(1, "assistant", "kept event", "e1", T0, "event:system");
        f.Post(2, "assistant", "removed event", "e2", T0.AddMinutes(3), "event:system");
        f.Compute.Transcript = [User("u1", "kept question", T0), Assistant("a1", T0.AddMinutes(1))];
        f.Answer(removed: ["u2"], from: T0.AddMinutes(2), at: T0.AddMinutes(10));

        Assert.Equal(200, (await f.PostAsync("u2")).Status);

        var export = await new ConversationExporter(f.Client, f.Discussions)
            .ExportSingleAsync((await f.Store.GetAsync(f.Id))!);
        Assert.Contains("kept question", export);
        Assert.DoesNotContain("removed event", export);
    }

    [Fact]
    public void A_record_is_removed_by_its_uid_or_by_falling_inside_the_window()
    {
        DiscussionMessage At(DateTimeOffset when, string? uid = null) => new(1, Guid.Empty, "user", "x",
            uid is null ? [] : new JsonObject { ["uid"] = uid }, when);
        var rollback = new DiscussionRollback("r", ["gone"], T0, T0.AddMinutes(5));

        Assert.False(rollback.Removes(At(T0.AddSeconds(-1))));
        Assert.True(rollback.Removes(At(T0)));
        Assert.True(rollback.Removes(At(T0.AddMinutes(4))));
        Assert.False(rollback.Removes(At(T0.AddMinutes(5))));
        Assert.True(rollback.Removes(At(T0.AddSeconds(-1), "gone")));
        Assert.True(rollback.Removes(At(T0.AddMinutes(9), "gone")));

        // Without the times only the uids can say.
        var uidsOnly = new DiscussionRollback("r", ["gone"], null, null);
        Assert.False(uidsOnly.Removes(At(T0)));
        Assert.True(uidsOnly.Removes(At(T0, "gone")));
    }

    private static SessionMessage User(string uid, string text, DateTimeOffset when) => new()
    {
        Role = "user", EventType = "text", Content = text, MessageUid = uid, Timestamp = when.UtcDateTime,
    };

    private static SessionMessage Assistant(string uid, DateTimeOffset when) => new()
    {
        Role = "assistant", EventType = "text", Content = "reply " + uid, MessageUid = uid, Timestamp = when.UtcDateTime,
    };

    private sealed class Fixture
    {
        public const string Session = "session-1";
        private const string Me = "me";

        public MemoryEntities Entities { get; } = new();
        public MemoryDiscussions Discussions { get; } = new();
        public FakeCompute Compute { get; } = new();
        public RecordingEvents Events { get; } = new();
        public DiscussionStore Store { get; }
        public RedComputeClient Client { get; }
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public LeafEntity Entity => Entities.Items.Single();
        public List<(string DiscussionId, string RollbackId)> Forgotten { get; } = [];
        public int FailForgets { get; set; }

        public Fixture(string? sessionId = Session, string owner = Me, bool confidential = false,
            string status = "idle", Guid? ownerPlugin = null, string type = "chat")
        {
            Store = new DiscussionStore(Entities, Discussions);
            Client = new RedComputeClient(Compute);
            var data = new JsonObject
            {
                ["discussion_id"] = Id,
                ["app"] = "nova",
                ["type"] = type,
                ["status"] = status,
                ["title"] = "A discussion",
                ["owner_id"] = owner,
                ["session_id"] = sessionId,
                ["conversation_revision"] = 0,
                ["read_conversation_revision"] = 0,
                ["last_processed_session_assistant_uid"] = "",
            };
            if (confidential)
            {
                data["confidential"] = true;
                data["agent"] = "agent";
            }
            if (ownerPlugin is not null) data["owner_plugin"] = ownerPlugin.Value.ToString();
            Entities.Items.Add(new LeafEntity(Guid.NewGuid(), "discussion", DiscussionStore.Slug(Id), "A discussion", data,
                T0, T0, "test"));
        }

        public void Post(long id, string role, string content, string? uid, DateTimeOffset when, string? source = null)
        {
            var metadata = new JsonObject();
            if (uid is not null) metadata["uid"] = uid;
            if (source is not null) metadata["source"] = source;
            Discussions.All.Add(new DiscussionMessage(id, Entity.Id, role, content, metadata, when));
        }

        public void PostNote(long id, string about, string kind, string? quotedUid, DateTimeOffset when)
        {
            var parts = new JsonArray(
                new JsonObject { ["type"] = "text", ["content"] = "a quote" },
                new JsonObject
                {
                    ["type"] = "event_data",
                    ["data"] = new JsonObject { ["discussionId"] = about, ["kind"] = kind, ["messageUid"] = quotedUid },
                });
            Discussions.All.Add(new DiscussionMessage(id, Entity.Id, "system", "a quote", new JsonObject
            {
                ["source"] = "event:discussion",
                ["parts_json"] = parts.ToJsonString(),
            }, when));
        }

        public void Answer(string[] removed, DateTimeOffset from, DateTimeOffset at, bool already = false)
        {
            Compute.RollbackStatus = HttpStatusCode.OK;
            Compute.RollbackAnswer = JsonSerializer.Serialize(new
            {
                rolledBack = true,
                rollbackId = "rollback-1",
                messageUid = "u2",
                epoch = "epoch-2",
                throughSequence = 9,
                removedMessageUids = removed,
                restoredText = "The refused message",
                fromCreatedAt = from,
                rolledBackAt = at,
                alreadyRolledBack = already,
                filesChanged = false,
            });
        }

        private static ClaimsPrincipal Signed(string subject, object identity) => new(new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("token_use", "execution"),
                new Claim("execution_identity", JsonSerializer.Serialize(identity)),
            ], "test"));

        /// <summary>The identities as the kernel signs them; <c>browser</c> is what Nova's own page holds.</summary>
        private static ClaimsPrincipal Caller(string caller)
        {
            var nova = new { id = "nova", name = "Nova" };
            var novaActor = new { kind = "app", id = "nova", name = "Nova" };
            var person = new { kind = "user", id = Me };
            var local = new { kind = "system", reason = "Browser work requested through the loopback LocalDefault identity" };
            var page = new[] { new { kind = "browser", route = "/apps/nova" } };
            object Actor(string kind) => new { kind, id = "worker", entityId = "agent", name = "Worker" };
            return caller switch
            {
                "browser" => Signed(Me, new { app = nova, actor = novaActor, beneficiary = person, context = page }),
                "another app's browser" => Signed(Me, new
                {
                    app = new { id = "codered" },
                    actor = new { kind = "app", id = "codered", name = "Code" },
                    beneficiary = person,
                    context = new[] { new { kind = "browser", route = "/apps/codered" } },
                }),
                "loopback browser" => Signed("local-user", new { app = nova, actor = novaActor, beneficiary = local, context = page }),
                "agent" or "owning agent" => Signed(Me, new { app = nova, actor = Actor("agent"), beneficiary = person, context = Array.Empty<object>() }),
                "agent in this discussion" => Signed(Me, new { app = nova, actor = Actor("agent"), beneficiary = person, context = new[] { new { kind = "discussion", id = "d1" } } }),
                "automation" => Signed(Me, new { app = nova, actor = Actor("automation"), beneficiary = person, context = page }),
                "agent with a browser context" => Signed(Me, new { app = nova, actor = Actor("agent"), beneficiary = person, context = page }),
                "derived child of a browser token" => Signed(Me, new { app = nova, actor = novaActor, beneficiary = person, context = page, parentExecutionId = "browser-token" }),
                "browser for someone else" => Signed(Me, new { app = nova, actor = novaActor, beneficiary = new { kind = "user", id = "someone-else" }, context = page }),
                "local subject claiming a user" => Signed("local-user", new { app = nova, actor = novaActor, beneficiary = new { kind = "user", id = "local-user" }, context = page }),
                "system browser token for a named subject" => Signed(Me, new { app = nova, actor = novaActor, beneficiary = local, context = page }),
                "no browser" => Signed(Me, new { app = nova, actor = novaActor, beneficiary = person, context = Array.Empty<object>() }),
                "plain login" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Me)], "test")),
                "service" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "service:nova")], "test")),
                "system" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "system")], "test")),
                "loopback without a token" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "local-user")], "LocalDefault")),
                _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null),
            };
        }

        public Task<IResult> CallAsync(string uid, string? id = null, string caller = "browser")
        {
            var ctx = new DefaultHttpContext();
            ctx.User = Caller(caller);
            return DiscussionRollbackEndpoints.RollbackAsync(id ?? Id, new DiscussionRollbackEndpoints.RollbackBody(uid),
                ctx, Store, Client, Events, (discussionId, rollback, _) =>
                {
                    if (FailForgets > 0)
                    {
                        FailForgets--;
                        throw new InvalidOperationException("LIVE said no");
                    }
                    Forgotten.Add((discussionId, rollback.RollbackId));
                    return Task.CompletedTask;
                });
        }

        public async Task<(int Status, JsonElement Body)> PostAsync(string uid, string? id = null, string caller = "browser")
        {
            var result = await CallAsync(uid, id, caller);
            var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
            return (status, result is ContentHttpResult content
                ? JsonSerializer.Deserialize<JsonElement>(content.ResponseContent!)
                : JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value));
        }
    }

    private sealed class FakeCompute : IComputeGateway
    {
        public HttpStatusCode RollbackStatus { get; set; } = HttpStatusCode.OK;
        public string RollbackAnswer { get; set; } = "{}";
        public List<string> RollbackUrls { get; } = [];
        public List<ComputeProvenance?> RollbackProvenance { get; } = [];
        public List<string> RollbackBodies { get; } = [];
        public bool Throw { get; set; }
        /// <summary>What a session read returns after the rollback; null when it cannot be read.</summary>
        public List<SessionMessage>? Transcript { get; set; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
        {
            if (Throw) throw new HttpRequestException("down");
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/rollback", StringComparison.Ordinal))
            {
                RollbackUrls.Add(url);
                RollbackProvenance.Add(provenance);
                RollbackBodies.Add(await request.Content!.ReadAsStringAsync(ct));
                return Json(RollbackStatus, RollbackAnswer);
            }
            if (Transcript is null) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                session = new { id = Session(), status = "Idle" },
                messages = Transcript.Select(message => new
                {
                    role = message.Role,
                    eventType = message.EventType,
                    content = message.Content,
                    messageUid = message.MessageUid,
                    timestamp = message.Timestamp,
                }),
            }));
        }

        private static string Session() => Fixture.Session;

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingEvents : IPluginEvents
    {
        public List<(string Type, JsonObject Payload)> Published { get; } = [];

        public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
        {
            Published.Add((eventType, payload));
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class MemoryEntities : IEntityStore
    {
        public List<LeafEntity> Items { get; } = [];
        public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Id == id));
        public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.Slug == slug));
        public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) =>
            Task.FromResult(Items.SingleOrDefault(entity => entity.TypeSlug == typeSlug && entity.Slug == slug));
        public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafEntity>>(Items.Where(entity => entity.TypeSlug == query.TypeSlug).ToList());
        public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LeafEntity> PatchAsync(Guid id, JsonObject patch, string? name = null, CancellationToken ct = default)
        {
            var index = Items.FindIndex(entity => entity.Id == id);
            var entity = Items[index];
            foreach (var (key, value) in patch) entity.Data[key] = value?.DeepClone();
            var updated = entity with { Name = name ?? entity.Name, UpdatedAt = DateTimeOffset.UtcNow };
            Items[index] = updated;
            return Task.FromResult(updated);
        }
        public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MemoryDiscussions : IDiscussions
    {
        public List<DiscussionMessage> All { get; } = [];
        public List<long> MarkedIds { get; } = [];
        public List<string> MarkedWith { get; } = [];
        public int FailMarks { get; set; }
        public IEnumerable<DiscussionMessage> Visible => All.Where(message => !MarkedIds.Contains(message.Id));

        public Task<int> MarkRolledBackAsync(Guid discussionId, IReadOnlyCollection<long> recordIds, string rollbackId, CancellationToken ct = default)
        {
            if (FailMarks > 0)
            {
                FailMarks--;
                throw new InvalidOperationException("the kernel said no");
            }
            var fresh = recordIds.Where(id => !MarkedIds.Contains(id)).ToList();
            MarkedIds.AddRange(fresh);
            MarkedWith.Add(rollbackId);
            return Task.FromResult(fresh.Count);
        }

        public Task<LeafEntity> CreateAsync(string? title, string? agentSlugOrId = null, JsonObject? data = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PostAsync(Guid discussionId, string role, string content, JsonObject? metadata = null, string? userId = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<DiscussionMessage>> GetMessagesAsync(Guid discussionId, int limit = 1000, long afterId = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>(Visible.Where(message => message.Id > afterId).OrderBy(message => message.Id).Take(limit).ToList());
        public Task<IReadOnlyList<DiscussionMessage>> SearchMessagesAsync(string query, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscussionMessage>>([]);
        public Task ClearMessagesAsync(Guid discussionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetReactionAsync(Guid discussionId, ReactionChange change, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LeafRecord>> GetReactionsAsync(Guid discussionId, DateTimeOffset? since = null, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LeafRecord>>([]);
        public IDisposable Subscribe(Guid discussionId, Func<DiscussionMessage, Task> onMessage) => new EmptyDisposable();

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
