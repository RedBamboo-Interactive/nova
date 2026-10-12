import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"

const panelSource = readFileSync(new URL("./voice-session-panel.tsx", import.meta.url), "utf8")
const hookSource = readFileSync(new URL("../hooks/use-voice-session.ts", import.meta.url), "utf8")
const cssSource = readFileSync(new URL("../plugin.css", import.meta.url), "utf8")

test("voice mode is an immersive modal backed by the active Agent portrait", () => {
  assert.match(panelSource, /fixed inset-0/)
  assert.match(panelSource, /role="dialog"/)
  assert.match(panelSource, /aria-modal="true"/)
  assert.match(panelSource, /nova-voice-mode__backdrop-image/)
  assert.match(panelSource, /nova-voice-mode__portrait-shell/)
  assert.match(panelSource, /data-visual-state=\{visualState\}/)
  assert.match(panelSource, /if \(event\.key === "Escape"\)/)
  assert.match(panelSource, /returnFocusRef\.current/)
  assert.match(panelSource, /returnTarget\?\.focus/)
})

test("voice mode keeps the closing phase mounted for its exit", () => {
  assert.match(hookSource, /const VOICE_MODE_EXIT_MS = 280/)
  assert.match(hookSource, /setPhase\("closing"\)/)
  assert.match(hookSource, /finishCloseAfterTransition\(\)/)
  assert.match(cssSource, /\.nova-voice-mode\[data-state="closing"\]/)
  assert.match(cssSource, /nova-voice-mode-exit 280ms/)
})

test("voice motion has a reduced-motion path", () => {
  assert.match(cssSource, /@media \(prefers-reduced-motion: reduce\)/)
  assert.match(cssSource, /\.nova-voice-mode__meter span/)
  assert.match(cssSource, /animation-duration: 1ms !important/)
})
