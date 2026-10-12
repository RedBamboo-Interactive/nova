import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"

const panelSource = readFileSync(new URL("./voice-session-panel.tsx", import.meta.url), "utf8")
const hookSource = readFileSync(new URL("../hooks/use-voice-session.ts", import.meta.url), "utf8")
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
  assert.match(panelSource, /variant="destructive"/)
  assert.match(panelSource, /ph-bold ph-phone-disconnect/)
  assert.doesNotMatch(panelSource, /ph-fill ph-phone-disconnect/)
  assert.match(cssSource, /--nova-voice-accent: var\(--primary\)/)
  assert.match(cssSource, /background: rgb\(from var\(--popover\)/)
  assert.match(cssSource, /border-radius: var\(--radius-xl\)/)
  assert.match(cssSource, /nova-voice-mode-enter 220ms cubic-bezier\(0\.16, 1, 0\.3, 1\)/)
})

test("voice mode projects the canonical discussion through the shared controlled chat", () => {
  assert.match(panelSource, /import \{[\s\S]*ChatPanel,[\s\S]*\} from "@redbamboo\/chat"/)
  assert.match(panelSource, /data-slot="voice-session-discussion"/)
  assert.match(panelSource, /<ChatPanel[\s\S]*messages=\{messages\}[\s\S]*isStreaming=\{isStreaming\}/)
  assert.match(panelSource, /sessionId=\{null\}/)
  assert.match(panelSource, /persistQueue=\{false\}/)
  assert.match(panelSource, /hideComposer/)
  assert.doesNotMatch(panelSource, /fetch\(|useChatStream|WebSocket/)
  assert.match(chatViewSource, /<VoiceSessionPanel[\s\S]*messages=\{activeMessages\}[\s\S]*isStreaming=\{isStreaming\}/)
})

test("canonical discussion is a bounded Leaf glass surface", () => {
  assert.match(cssSource, /\.nova-voice-mode__discussion \{/)
  assert.match(cssSource, /\.nova-voice-mode__discussion[\s\S]*min-height: 10rem/)
  assert.match(cssSource, /\.nova-voice-mode__chat-panel \{[\s\S]*height: 100%/)
  assert.match(cssSource, /@media \(max-height: 700px\)[\s\S]*\.nova-voice-mode__discussion/)
})

test("voice mode keeps the closing phase mounted for its exit", () => {
  assert.match(hookSource, /const VOICE_MODE_EXIT_MS = 160/)
  assert.match(hookSource, /setPhase\("closing"\)/)
  assert.match(hookSource, /finishCloseAfterTransition\(\)/)
  assert.match(cssSource, /\.nova-voice-mode\[data-state="closing"\]/)
  assert.match(cssSource, /nova-voice-mode-exit 160ms/)
})

test("voice motion has a reduced-motion path", () => {
  assert.match(cssSource, /@media \(prefers-reduced-motion: reduce\)/)
  assert.match(cssSource, /\.nova-voice-mode__meter span/)
  assert.match(cssSource, /animation-duration: 1ms !important/)
})
