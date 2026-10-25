using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Natural Voice must hear what canonical Nova says on her own (watcher wakes, automations)
// exactly once, and must never re-speak a delegation result or echo its own speech.
public sealed class VoiceBackendForwarderTests
{
    private const string Discussion = "discussion-1";
    private const string AiSession = "ai-1";
    private const string Voice = "vs_1";

    [Fact]
    public async Task Each_settled_backend_final_is_spoken_exactly_once()
    {
        var (forwarder, sent) = Open(Snapshot("Idle", User(1, "u1", "Let's play"), Assistant(2, "a1", "Ready.")));

        var settled = Snapshot("Idle", User(1, "u1", "Let's play"), Assistant(2, "a1", "Ready."),
            Assistant(3, "a2", "My turn's done. 19 HP, 17 Doom."));
        await forwarder.OnSettledAsync(Discussion, AiSession, settled);
        await forwarder.OnSettledAsync(Discussion, AiSession, settled);

        var update = Assert.Single(sent);
        Assert.Equal((Voice, "assistant:a2", "speak"), (update.VoiceSessionId, update.UpdateId, update.Mode));
        Assert.Equal("My turn's done. 19 HP, 17 Doom.", update.Content);
    }

    [Fact]
    public async Task A_silent_settle_is_never_spoken()
    {
        var (forwarder, sent) = Open(Snapshot("Idle", Assistant(1, "a1", "Ready.")));

        await forwarder.OnSettledAsync(Discussion, AiSession,
            Snapshot("Idle", Assistant(1, "a1", "Ready."), Assistant(2, "a2", NovaNoReply.Marker + "\n")));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task History_the_voice_already_received_is_never_spoken_even_beyond_the_admission_tail()
    {
        var (forwarder, sent) = Open(Snapshot("Idle", Assistant(50, "a50", "Recent.")));

        await forwarder.OnSettledAsync(Discussion, AiSession,
            Snapshot("Idle", Assistant(10, "a10", "Old, outside the tail."), Assistant(50, "a50", "Recent.")));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task No_binding_means_no_call_and_other_discussions_are_ignored()
    {
        var sent = new List<VoiceBackendUpdate>();
        var forwarder = new VoiceBackendForwarder();
        forwarder.AttachSender((update, _) => { sent.Add(update); return Task.FromResult(true); });

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle", Assistant(3, "a2", "Hello.")));
        forwarder.Open(Voice, Discussion, AiSession, Snapshot("Idle"));
        await forwarder.OnSettledAsync("discussion-2", AiSession, Snapshot("Idle", Assistant(3, "a2", "Hello.")));
        await forwarder.OnSettledAsync(Discussion, "other-ai", Snapshot("Idle", Assistant(3, "a2", "Hello.")));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Closed_binding_stops_forwarding()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));
        forwarder.Close(Voice);

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle", Assistant(3, "a2", "Hello.")));
        await forwarder.OnDirectAssistantMessageAsync(Discussion, "n1", "Proactive hello.");

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Turns_that_are_not_settled_are_not_spoken()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Active", Assistant(3, "a2", "Partial")));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Voice_session_turns_and_text_free_tool_turns_are_skipped()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            User(1, $"voice:{Voice}:1:user", "How's it going?"),
            Assistant(2, $"voice:{Voice}:2:assistant", "Pretty good!"),
            new SessionMessage { Id = 3, Sequence = 3, Role = "assistant", EventType = "tool_use",
                ToolName = "Bash", MessageUid = "tools-only" },
            Assistant(4, "blank", "   ")));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Delegation_results_are_left_to_the_delegation_while_pending_and_after()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));
        const string prompt = $"voice:{Voice}:item_1:user";
        forwarder.DelegationStarted(Voice, [prompt]);

        var answered = Snapshot("Idle", User(1, prompt, "What floor are we on?"), Assistant(2, "a1", "Floor 12."));
        await forwarder.OnSettledAsync(Discussion, AiSession, answered);
        Assert.Empty(sent);

        forwarder.DelegationFinished(Voice, [prompt], ["a1"]);
        await forwarder.OnSettledAsync(Discussion, AiSession, answered);
        Assert.Empty(sent);

        // A later watcher-wake turn without a new user message is Nova's own update.
        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            User(1, prompt, "What floor are we on?"), Assistant(2, "a1", "Floor 12."),
            Assistant(3, "a2", "Elite fight started.")));
        Assert.Equal("assistant:a2", Assert.Single(sent).UpdateId);
    }

    [Fact]
    public async Task Several_finals_speak_the_newest_and_send_the_rest_as_context()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            User(1, "event-1", "<nova-event>phase</nova-event>"), Assistant(2, "a1", "Turn 1 done."),
            User(3, "event-2", "<nova-event>phase</nova-event>"), Assistant(4, "a2", "Turn 2 done.")));

        Assert.Equal([("assistant:a1", "context"), ("assistant:a2", "speak")],
            sent.Select(update => (update.UpdateId, update.Mode)).ToList());
    }

    [Fact]
    public async Task Final_phase_wins_over_commentary()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            Assistant(2, "a1", "Let me look at the map.", "commentary"),
            Assistant(3, "a1", "We're at the rest site.", "final")));

        Assert.Equal("We're at the rest site.", Assert.Single(sent).Content);
    }

    [Fact]
    public async Task A_turn_running_at_admission_is_spoken_when_it_settles()
    {
        var (forwarder, sent) = Open(Snapshot("Active",
            User(1, "u1", "Play your turn"), Assistant(2, "a1", "Playing Blight Strike", "commentary")));

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            User(1, "u1", "Play your turn"), Assistant(2, "a1", "Playing Blight Strike", "commentary"),
            Assistant(3, "a1", "Done, it's at 54 with 53 Doom.", "final")));

        Assert.Equal("assistant:a1", Assert.Single(sent).UpdateId);
    }

    [Fact]
    public async Task Without_an_admission_baseline_the_first_settle_only_establishes_history()
    {
        var (forwarder, sent) = Open(initial: null);

        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle", Assistant(2, "a1", "Old.")));
        Assert.Empty(sent);
        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            Assistant(2, "a1", "Old."), Assistant(3, "a2", "New.")));
        Assert.Equal("assistant:a2", Assert.Single(sent).UpdateId);
    }

    [Fact]
    public async Task Failed_delivery_is_retried_on_the_next_settle()
    {
        var attempts = 0;
        var sent = new List<VoiceBackendUpdate>();
        var forwarder = new VoiceBackendForwarder();
        forwarder.AttachSender((update, _) =>
        {
            if (++attempts == 1) return Task.FromResult(false);
            sent.Add(update);
            return Task.FromResult(true);
        });
        forwarder.Open(Voice, Discussion, AiSession, Snapshot("Idle"));
        var settled = Snapshot("Idle", Assistant(3, "a2", "Hello."));

        await forwarder.OnSettledAsync(Discussion, AiSession, settled);
        await forwarder.OnSettledAsync(Discussion, AiSession, settled);
        await forwarder.OnSettledAsync(Discussion, AiSession, settled);

        Assert.Single(sent);
    }

    [Fact]
    public async Task Direct_nova_message_is_spoken_once_and_not_again_when_the_session_settles()
    {
        var (forwarder, sent) = Open(Snapshot("Idle"));

        await forwarder.OnDirectAssistantMessageAsync(Discussion, "n1", "Laurent, the render finished.");
        await forwarder.OnDirectAssistantMessageAsync(Discussion, "n1", "Laurent, the render finished.");
        await forwarder.OnSettledAsync(Discussion, AiSession, Snapshot("Idle",
            Assistant(3, "n1", "Laurent, the render finished.")));

        var update = Assert.Single(sent);
        Assert.Equal(("assistant:n1", "speak"), (update.UpdateId, update.Mode));
    }

    [Fact]
    public void Spoken_content_drops_markdown_code_links_and_paths()
    {
        var spoken = VoiceBackendForwarder.PrepareSpokenContent("""
            ## Turn done
            **Boss** is at *19 HP* with `17` Doom. See [the log](https://example.com/log) and
            `T:/Projects/nova/src/Leaf.Plugins.Nova/VoiceSessionService.cs`.

            ```powershell
            Invoke-RestMethod http://127.0.0.1:18800/jobs
            ```
            - Next: rest site
            | a | b |
            |---|---|
            """);

        Assert.DoesNotContain("#", spoken);
        Assert.DoesNotContain("*", spoken);
        Assert.DoesNotContain("`", spoken);
        Assert.DoesNotContain("http", spoken);
        Assert.DoesNotContain("Invoke-RestMethod", spoken);
        Assert.DoesNotContain("T:/Projects", spoken);
        Assert.DoesNotContain("|", spoken);
        Assert.Contains("Boss is at 19 HP with 17 Doom.", spoken);
        Assert.Contains("the log", spoken);
        Assert.Contains("Next: rest site", spoken);
    }

    [Fact]
    public void Long_content_is_bounded_at_a_sentence_for_speech_and_context()
    {
        var text = string.Concat(Enumerable.Repeat("This is one sentence about the run. ", 100));

        var spoken = VoiceBackendForwarder.PrepareSpokenContent(text);
        var context = VoiceBackendForwarder.PrepareContextContent(text);

        Assert.True(spoken.Length <= VoiceBackendForwarder.SpokenMaxChars);
        Assert.EndsWith(".", spoken);
        Assert.True(context.Length <= VoiceBackendForwarder.ContextMaxChars);
    }

    private static (VoiceBackendForwarder, List<VoiceBackendUpdate>) Open(SessionSnapshot? initial)
    {
        var sent = new List<VoiceBackendUpdate>();
        var forwarder = new VoiceBackendForwarder();
        forwarder.AttachSender((update, _) => { sent.Add(update); return Task.FromResult(true); });
        forwarder.Open(Voice, Discussion, AiSession, initial);
        return (forwarder, sent);
    }

    private static SessionSnapshot Snapshot(string status, params SessionMessage[] messages)
        => new(status, null, null, messages.ToList());

    private static SessionMessage User(long id, string uid, string content)
        => new() { Id = id, Sequence = id, Role = "user", EventType = "text", Content = content, MessageUid = uid };

    private static SessionMessage Assistant(long id, string uid, string content, string? phase = null)
        => new() { Id = id, Sequence = id, Role = "assistant", EventType = "text", Content = content,
            MessageUid = uid, Phase = phase };
}
