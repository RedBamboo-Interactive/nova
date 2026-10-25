namespace Leaf.Plugins.Nova.Tests;

// NovaContextBuilder output for NovaContextFrameTests.Representative(), captured from the builder
// before every interpolated value went through NovaEventText (Nova 5bc8b93). Ordinary context
// must keep rendering exactly this.
//
// Deliberate changes since (ab7a1a07, reactions both ways): the opening tag names the incoming
// message (message="..."); the reaction recipe carries the execution bearer, the current
// discussion, a JSON-escaped emoji and no hand-passed agentId; the full context renders its
// reaction lines (it used to drop them); reaction lines say add or remove, the agent's latest or an
// earlier message, and quote it; the full context ends with the <nova-no-reply/> sentence.
internal static class NovaContextGolden
{
    internal const string Full = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="text" discussion="c0ffee01" message="5d1f0c2a9b8e47c3a6f4e2d1c0b9a876" agent="Nova" room="Living room">
        Mood: Curious, a little tired
        Active discussions:
        - [c0ffee01] "Plan "v2" & rollout <beta>" . 12 msgs . 5min ago <- you are here
        - [LIVE][live0001] "Live · 2026-10-24" . 40 msgs . 1min ago
        - [fam00001] "Family trip 👨‍👩‍👧 ‏שלום" . 3 msgs . 2h ago

        Recently archived:
        - [old00001] "Archived notes" . 8 msgs . archived 3d ago

        Other agents' active discussions:
        - [ag2disc1] "Storyboard: chapter 3" . 5 msgs . 30min ago

        Recall any discussion: curl -s http://127.0.0.1:18804/api/apps/nova/discussions/{id}/export
        Search all discussions: curl -s "http://127.0.0.1:18804/api/apps/nova/discussions/search?q={query}"
        Get full context snapshot: curl -s http://127.0.0.1:18804/api/apps/nova/discussions/{id}/context
        React to a message (messageKey: the message="..." uid on a context tag): curl -s -X POST http://127.0.0.1:18804/api/apps/nova/discussions/c0ffee01/reactions -H "Authorization: Bearer $REDLEAF_EXECUTION_TOKEN" -H "Content-Type: application/json" -d '{"emoji":"\u2764\ufe0f","messageKey":"<message uid>"}'
        Post to LIVE timeline: curl -s -X POST http://127.0.0.1:18804/api/apps/nova/discussions/live0001/event -H "Content-Type: application/json" -d '{"content":"...","source":"..."}'

        Recent LIVE events:
        - [sonos] Now playing in Kitchen: Rock & Roll <Live> — Led Zeppelin (10min ago)
        - [automation] Morning brief completed: 12°C "light rain"; 2 meetings (8min ago)
        - [discussion] Laurent in "Plan v2": can we ship today? (6min ago)

        Installed extension context:
        - [mail] 3 unread: "Invoice #42" from ACME <billing@acme.test>
        - [presence] Laurent is home (Living room)

        Reactions since your last message:
        - Laurent reacted 👍 to your latest message: "Shipped & tested"
        - Laurent removed ❤️ from an earlier message of yours: "Draft <v1> plan"

        Wearing: navy hoodie & grey jeans

        When a reaction or nothing at all says it better than words, make your entire reply exactly <nova-no-reply/>: the turn then ends with no visible message.
        </nova-context>
        """;

    internal const string Delta = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="text" discussion="c0ffee01" message="5d1f0c2a9b8e47c3a6f4e2d1c0b9a876" agent="Nova" room="Living room">
        Changes since last message:
        - New discussion [fam00001] "Family trip 👨‍👩‍👧 ‏שלום" (thinking)
        - New discussion [old00001] "Archived notes" (archived)
        - Discussion [gone0001] "Gone & done" no longer visible
        - Discussion [c0ffee01] "Plan "v2" & rollout <beta>" changed status: thinking -> idle
        - 2 new messages in [c0ffee01] "Plan "v2" & rollout <beta>"
        - Discussion [c0ffee01] renamed to "Plan "v2" & rollout <beta>"
        - 2 new messages in [live0001] "Live · 2026-10-24"
        - New discussion from another agent: [ag2disc1] "Storyboard: chapter 3"
        - Other agent discussion [ag2old01] "Storyboard: chapter 2" no longer active
        - Outfit changed: Wearing: navy hoodie & grey jeans
        - Mood changed: Curious, a little tired
        - [sonos] Now playing in Kitchen: Rock & Roll <Live> — Led Zeppelin
        - [automation] Morning brief completed: 12°C "light rain"; 2 meetings
        - [mail] 3 unread: "Invoice #42" from ACME <billing@acme.test>
        - [presence] Laurent is home (Living room)
        - Extension context [weather] removed
        - Laurent reacted 👍 to your latest message: "Shipped & tested"
        - Laurent removed ❤️ from an earlier message of yours: "Draft <v1> plan"
        </nova-context>
        """;

    // A reaction note carries no message of its own (no message="..."), only the reaction lines and
    // one constant sentence: a triggered turn may settle silently, a steered note needs no reply.
    internal const string TriggeredNote = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="reaction" discussion="c0ffee01" agent="Nova" room="Living room">
        Reactions since your last message:
        - Laurent reacted 👍 to your latest message: "Shipped & tested"
        - Laurent removed ❤️ from an earlier message of yours: "Draft <v1> plan"
        No one wrote anything. A reaction-only or silent reply (exactly <nova-no-reply/>) is fine when words would add nothing.
        </nova-context>
        """;

    internal const string SteeredNote = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="reaction" discussion="c0ffee01" agent="Nova" room="Living room">
        Reactions since your last message:
        - Laurent reacted 👍 to your latest message: "Shipped & tested"
        - Laurent removed ❤️ from an earlier message of yours: "Draft <v1> plan"
        This arrived while you were working; take it into account, it needs no reply of its own.
        </nova-context>
        """;
}
