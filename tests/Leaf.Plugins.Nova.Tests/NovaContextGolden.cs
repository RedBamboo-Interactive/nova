namespace Leaf.Plugins.Nova.Tests;

// NovaContextBuilder output for NovaContextFrameTests.Representative(), captured from the builder
// before every interpolated value went through NovaEventText (Nova 5bc8b93). Ordinary context
// must keep rendering exactly this.
internal static class NovaContextGolden
{
    internal const string Full = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="text" discussion="c0ffee01" agent="Nova" room="Living room">
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
        React to a message: curl -s -X POST http://127.0.0.1:18804/api/apps/nova/discussions/{id}/reactions -H "Content-Type: application/json" -d '{"emoji":"...","messageKey":"...","agentId":"...","agentName":"..."}'
        Post to LIVE timeline: curl -s -X POST http://127.0.0.1:18804/api/apps/nova/discussions/live0001/event -H "Content-Type: application/json" -d '{"content":"...","source":"..."}'

        Recent LIVE events:
        - [sonos] Now playing in Kitchen: Rock & Roll <Live> — Led Zeppelin (10min ago)
        - [automation] Morning brief completed: 12°C "light rain"; 2 meetings (8min ago)
        - [discussion] Laurent in "Plan v2": can we ship today? (6min ago)

        Installed extension context:
        - [mail] 3 unread: "Invoice #42" from ACME <billing@acme.test>
        - [presence] Laurent is home (Living room)

        Wearing: navy hoodie & grey jeans
        </nova-context>
        """;

    internal const string Delta = """
        <nova-context timestamp="2026-10-24T14:30:00Z" day="Saturday" device="Pixel 9" device_type="phone" platform="android" input="text" discussion="c0ffee01" agent="Nova" room="Living room">
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
        - Laurent reacted 👍 to your message "Shipped & tested"
        </nova-context>
        """;
}
