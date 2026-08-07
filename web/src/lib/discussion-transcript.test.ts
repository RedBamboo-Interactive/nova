import { test } from "node:test"
import assert from "node:assert/strict"
import { discussionMessagesForMerge, mergeRevalidatedMessages } from "./discussion-transcript.ts"
import type { DiscussionMessage } from "./types.ts"
import type { MessageBlock } from "@redbamboo/chat"

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
const acceptedBridge = message("accepted-user", "user-message")

test("uses raw session fidelity without duplicating the discussion transcript", () => {
  assert.deepEqual(discussionMessagesForMerge([event, reply], true), [event])
})

test("retains the authorized discussion transcript when the raw session is unavailable", () => {
  assert.deepEqual(discussionMessagesForMerge([event, reply], false), [event, reply])
})

test("retains an accepted user bridge while raw session history is available", () => {
  assert.deepEqual(
    discussionMessagesForMerge([event, reply, acceptedBridge], true),
    [event, acceptedBridge],
  )
})

function block(id: string, content: string, timestamp = "2026-08-05T10:00:00.000Z"): MessageBlock {
  return { id, role: "assistant", parts: [{ type: "text", content }], timestamp }
}

test("revalidation replaces an unchanged stale snapshot with the authoritative tail", () => {
  const old = block("old", "old")
  const fresh = block("fresh", "fresh", "2026-08-05T10:01:00.000Z")

  assert.deepEqual(mergeRevalidatedMessages([old, fresh], [old], [old]), [old, fresh])
})

test("revalidation preserves a message added while the request was in flight", () => {
  const old = block("old", "old")
  const fresh = block("fresh", "fresh", "2026-08-05T10:01:00.000Z")
  const optimistic = block("optimistic", "sending", "2026-08-05T10:02:00.000Z")

  assert.deepEqual(
    mergeRevalidatedMessages([old, fresh], [old], [old, optimistic]),
    [old, fresh, optimistic],
  )
})

test("accepted send replaces the queue winner's optimistic user block", () => {
  const optimistic: MessageBlock = {
    ...block("optimistic", "hello"),
    role: "user",
  }
  const accepted: MessageBlock = {
    ...block("message-uid", "hello", "2026-08-05T10:00:00.050Z"),
    role: "user",
  }

  assert.deepEqual(
    mergeRevalidatedMessages([accepted], [optimistic], [optimistic]),
    [accepted],
  )
})

test("revalidation keeps a concurrent stream update over an older fetched block", () => {
  const before = block("turn", "calling tool")
  const fetched = block("turn", "calling tool")
  const streamed = block("turn", "tool completed")

  assert.deepEqual(mergeRevalidatedMessages([fetched], [before], [streamed]), [streamed])
})
