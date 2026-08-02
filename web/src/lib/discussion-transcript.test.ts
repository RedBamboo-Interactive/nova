import { test } from "node:test"
import assert from "node:assert/strict"
import { discussionMessagesForMerge } from "./discussion-transcript.ts"
import type { DiscussionMessage } from "./types.ts"

function message(id: string, source: string): DiscussionMessage {
  return {
    id,
    role: source === "session-transcript" ? "assistant" : "user",
    parts: [{ type: "text", content: id }],
    timestamp: "2026-08-02T12:00:00.000Z",
    source,
  }
}

const event = message("tick", "event:heartbeat-tick")
const reply = message("reply", "session-transcript")

test("uses raw session fidelity without duplicating the discussion transcript", () => {
  assert.deepEqual(discussionMessagesForMerge([event, reply], true), [event])
})

test("retains the authorized discussion transcript when the raw session is unavailable", () => {
  assert.deepEqual(discussionMessagesForMerge([event, reply], false), [event, reply])
})
