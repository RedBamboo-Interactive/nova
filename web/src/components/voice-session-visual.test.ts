import test from "node:test"
import assert from "node:assert/strict"
import { resolveVoiceSessionVisualState, voiceSessionStatusLabel } from "./voice-session-visual.ts"

const connected = {
  phase: "connected" as const,
  muted: false,
  delegating: false,
  assistantActive: false,
  userActive: false,
}

test("voice visual state follows conversational activity", () => {
  assert.equal(resolveVoiceSessionVisualState(connected), "listening")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, userActive: true }), "listening")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, assistantActive: true }), "speaking")
  assert.equal(voiceSessionStatusLabel("speaking"), "Speaking")
})

test("transport and control states take precedence over cosmetic activity", () => {
  assert.equal(resolveVoiceSessionVisualState({ ...connected, muted: true, assistantActive: true }), "muted")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, delegating: true, muted: true }), "working")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, phase: "degraded", delegating: true }), "interrupted")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, phase: "error" }), "error")
  assert.equal(resolveVoiceSessionVisualState({ ...connected, phase: "closing" }), "ending")
})
