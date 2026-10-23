import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"

const panelSource = readFileSync(new URL("./voice-session-panel.tsx", import.meta.url), "utf8")
const hookSource = readFileSync(new URL("../hooks/use-voice-session.ts", import.meta.url), "utf8")
const wakeLockSource = readFileSync(new URL("../hooks/use-screen-wake-lock.ts", import.meta.url), "utf8")
const cssSource = readFileSync(new URL("../plugin.css", import.meta.url), "utf8")
const chatViewSource = readFileSync(new URL("../panels/ChatView.tsx", import.meta.url), "utf8")

test("voice mode is an immersive modal backed by the active Agent portrait", () => {
  assert.match(panelSource, /fixed inset-0/)
  assert.match(panelSource, /role="dialog"/)
  assert.match(panelSource, /aria-modal="true"/)
  assert.match(panelSource, /nova-voice-mode__backdrop-image/)
  assert.match(panelSource, /nova-voice-mode__portrait-shell/)
  assert.match(panelSource, /data-visual-state=\{visualState\}/)
  assert.match(panelSource, /if \(event\.key === "Escape"\)/)
  assert.match(panelSource, /panelRef\.current\?\.contains\(event\.target as Node\)/)
  assert.match(panelSource, /returnFocusRef\.current/)
  assert.match(panelSource, /returnTarget\?\.focus/)
  assert.doesNotMatch(panelSource, /GPT-Live · Nova memory and tools/)
})

test("voice controls and surfaces use the Leaf design language", () => {
  assert.match(panelSource, /import \{ Button \} from "@redbamboo\/ui"/)
  assert.match(panelSource, /<Button[\s\S]*variant="outline"/)
  assert.match(panelSource, /canStart \? "default" : "destructive"/)
  assert.match(panelSource, /aria-label=\{canStart \? "Start voice call" : "End voice session"\}/)
  assert.match(panelSource, /canStart \? "ph-phone" : "ph-phone-disconnect"/)
  assert.doesNotMatch(panelSource, /ph-fill ph-phone-disconnect/)
  assert.match(cssSource, /--nova-voice-accent: var\(--primary\)/)
  assert.match(cssSource, /background: rgb\(from var\(--popover\)/)
  assert.match(cssSource, /border-radius: 999px/)
  assert.match(cssSource, /nova-voice-mode-enter 220ms cubic-bezier\(0\.16, 1, 0\.3, 1\)/)
})

test("opening voice mode is free and only the green Call control starts admission", () => {
  assert.match(hookSource, /const open = useCallback\(\(\) => \{[\s\S]*setPhase\("ready"\)/)
  assert.match(hookSource, /if \(discussionRef\.current && discussionRef\.current !== discussionId\) void stop\(\)/)
  assert.match(chatViewSource, /const openVoice = useCallback\(\(\) => \{ voiceSession\.open\(\) \}/)
  assert.match(chatViewSource, /onVoice=\{owningApp \? undefined : openVoice\}/)
  assert.match(panelSource, /onClick=\{canStart \? onStart : onEnd\}/)
  assert.match(cssSource, /--nova-voice-call: #22c55e/)
  assert.match(cssSource, /\.nova-voice-mode__control--call[\s\S]*background: var\(--nova-voice-call\)/)
})

test("voice mode projects the canonical discussion through the shared controlled chat", () => {
  assert.match(panelSource, /import \{[\s\S]*ChatPanel,[\s\S]*\} from "@redbamboo\/chat"/)
  assert.match(panelSource, /data-slot="voice-session-discussion"/)
  assert.match(panelSource, /<ChatPanel[\s\S]*messages=\{displayMessages\}[\s\S]*isStreaming=\{isStreaming\}/)
  assert.match(panelSource, /sessionId=\{null\}/)
  assert.match(panelSource, /persistQueue=\{false\}/)
  assert.match(panelSource, /hideComposer/)
  assert.doesNotMatch(panelSource, /fetch\(|useChatStream|WebSocket/)
  assert.match(chatViewSource, /<VoiceSessionPanel[\s\S]*messages=\{activeMessages\}[\s\S]*isStreaming=\{isStreaming\}/)
})

test("live captions are transient native chat bubbles rather than a parallel visual transcript", () => {
  assert.match(panelSource, /projectVoiceCaption\(messages/)
  assert.match(panelSource, /messages=\{displayMessages\}/)
  assert.match(panelSource, /aria-live="polite"/)
  assert.doesNotMatch(panelSource, /nova-voice-mode__caption/)
})

test("canonical discussion floats directly on the backdrop and fades before the portrait", () => {
  assert.match(cssSource, /\.nova-voice-mode__discussion \{/)
  assert.match(cssSource, /\.nova-voice-mode__discussion[\s\S]*min-height: 10rem/)
  assert.match(cssSource, /\.nova-voice-mode__discussion-body[\s\S]*mask-image: linear-gradient\(to bottom, transparent 0, #000 3\.5rem/)
  assert.match(cssSource, /\.nova-voice-mode__chat-panel \{[\s\S]*height: 100%/)
  assert.match(cssSource, /@media \(max-height: 700px\)[\s\S]*\.nova-voice-mode__discussion/)
  assert.doesNotMatch(panelSource, /nova-voice-mode__discussion-header/)
  assert.doesNotMatch(cssSource, /\.nova-voice-mode__discussion-header/)
})

test("voice mode keeps the closing phase mounted for its exit", () => {
  assert.match(hookSource, /const VOICE_MODE_EXIT_MS = 160/)
  assert.match(hookSource, /setPhase\("closing"\)/)
  assert.match(hookSource, /finishCloseAfterTransition\(\)/)
  assert.match(cssSource, /\.nova-voice-mode\[data-state="closing"\]/)
  assert.match(cssSource, /nova-voice-mode-exit 160ms/)
})

test("voice mode keeps the visible screen awake without weakening background close", () => {
  assert.match(hookSource, /useScreenWakeLock\(keepScreenAwake\)/)
  assert.match(hookSource, /phase === "starting" \|\| phase === "connected" \|\| phase === "degraded"/)
  assert.match(wakeLockSource, /navigator\.wakeLock/)
  assert.match(wakeLockSource, /request\("screen"\)/)
  assert.match(wakeLockSource, /generation !== this\.generation/)
  assert.match(hookSource, /document\.visibilityState === "hidden"\) void stop\(\)/)
  assert.match(hookSource, /window\.addEventListener\("pagehide", closeWhenPageLeaves\)/)
})

test("voice motion has a reduced-motion path", () => {
  assert.match(cssSource, /@media \(prefers-reduced-motion: reduce\)/)
  assert.match(cssSource, /\.nova-voice-mode__meter span/)
  assert.match(cssSource, /animation-duration: 1ms !important/)
})
