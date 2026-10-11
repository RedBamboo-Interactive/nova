import test from "node:test"
import assert from "node:assert/strict"
import { applyVoiceEvent } from "./voice-session-events.ts"

test("voice captions preserve exact delta spacing and switch speakers", () => {
  let state = { speaker: null as "user" | "assistant" | null, user: "", assistant: "" }
  state = applyVoiceEvent(state, { type: "session.input_transcript.delta", delta: "Hello " })
  state = applyVoiceEvent(state, { type: "session.input_transcript.delta", delta: "Nova" })
  assert.equal(state.user, "Hello Nova")
  state = applyVoiceEvent(state, { type: "session.output_transcript.delta", delta: "Hey." })
  assert.equal(state.speaker, "assistant")
  assert.equal(state.assistant, "Hey.")
  assert.equal(state.user, "")
})

test("unknown events do not disturb captions", () => {
  const state = { speaker: "assistant" as const, user: "", assistant: "Hi" }
  assert.deepEqual(applyVoiceEvent(state, { type: "session.started" }), state)
})
