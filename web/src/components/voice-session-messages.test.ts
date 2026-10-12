import test from "node:test"
import assert from "node:assert/strict"
import type { MessageBlock } from "@redbamboo/chat"
import { projectVoiceCaption } from "./voice-session-messages.ts"

const canonical: MessageBlock[] = [{
  id: "canonical",
  role: "assistant",
  timestamp: "2026-10-12T18:00:00Z",
  parts: [
    { type: "tool_use", toolName: "Read", toolInput: "{}", content: "" },
    { type: "tool_result", toolName: "Read", content: "done" },
    { type: "text", content: "The canonical answer." },
  ],
}]

test("projects the active user caption as a transient ordinary chat block", () => {
  const projected = projectVoiceCaption(canonical, {
    speaker: "user",
    userCaption: "Can you hear me?",
    assistantCaption: "",
    timestamp: "2026-10-12T18:00:01Z",
  })
  assert.notEqual(projected, canonical)
  assert.equal(projected.length, 2)
  assert.equal(projected[1]?.role, "user")
  assert.equal(projected[1]?.id, "voice-caption-user")
  assert.equal(projected[1]?.metadata?.transientVoiceCaption, true)
  assert.equal(projected[1]?.parts[0]?.content, "Can you hear me?")
  assert.equal(projected[1]?.parts[0]?.isPartial, false)
  assert.equal(canonical.length, 1, "canonical input remains untouched")
  assert.equal(canonical[0]?.parts.length, 3, "tool activity remains untouched")
})

test("projects assistant speech as streaming text without inventing canonical state", () => {
  const projected = projectVoiceCaption(canonical, {
    speaker: "assistant",
    userCaption: "",
    assistantCaption: "Yes, clearly.",
    timestamp: "2026-10-12T18:00:02Z",
  })
  assert.equal(projected[1]?.role, "assistant")
  assert.equal(projected[1]?.id, "voice-caption-assistant")
  assert.equal(projected[1]?.parts[0]?.isPartial, true)
})

test("does not duplicate an equal canonical tail or allocate for silence", () => {
  const duplicate = projectVoiceCaption(canonical, {
    speaker: "assistant",
    userCaption: "",
    assistantCaption: "  The   canonical answer. ",
    timestamp: "2026-10-12T18:00:03Z",
  })
  assert.equal(duplicate, canonical)
  const silent = projectVoiceCaption(canonical, {
    speaker: null,
    userCaption: "",
    assistantCaption: "",
    timestamp: "2026-10-12T18:00:04Z",
  })
  assert.equal(silent, canonical)
})
