import { test } from "node:test"
import assert from "node:assert/strict"
import {
  mergeDiscussionAndSessionBlocks,
  mergeRevalidatedMessages,
} from "./discussion-transcript.ts"
import type { MessageBlock } from "@redbamboo/chat"

function projectedBlock(id: string, source: string, timestamp: string): MessageBlock {
  return {
    id,
    role: source === "session-transcript" ? "assistant" : "user",
    parts: [{ type: "text", content: id }],
    timestamp,
    metadata: { source },
  }
}

const event = projectedBlock("tick", "event:heartbeat-tick", "2026-08-02T12:00:00.000Z")
const projectedReply = projectedBlock("reply", "session-transcript", "2026-08-02T12:01:00.000Z")
const acceptedBridge = projectedBlock("accepted-user", "user-message", "2026-08-02T12:02:00.000Z")
const automationOpening: MessageBlock = {
  id: "automation-opening",
  role: "assistant",
  parts: [{ type: "text", content: "A persisted opening" }],
  timestamp: "2026-08-02T11:59:00.000Z",
  metadata: { source: "nova-message" },
}
const rawReply: MessageBlock = {
  ...projectedReply,
  parts: [{ type: "tool_use", content: "", toolName: "Read", toolInput: "{}" }],
  metadata: undefined,
}

test("uses raw session fidelity without duplicating the discussion transcript", () => {
  assert.deepEqual(
    mergeDiscussionAndSessionBlocks([event, projectedReply], [rawReply]),
    [event, rawReply],
  )
})

test("retains the authorized discussion transcript when the raw session is unavailable", () => {
  assert.deepEqual(
    mergeDiscussionAndSessionBlocks([event, projectedReply], []),
    [event, projectedReply],
  )
})

test("retains an accepted user bridge while raw session history is available", () => {
  assert.deepEqual(
    mergeDiscussionAndSessionBlocks([event, projectedReply, acceptedBridge], [rawReply]),
    [event, rawReply, acceptedBridge],
  )
})

test("retains a persisted automation opening when its session replay is absent", () => {
  assert.deepEqual(
    mergeDiscussionAndSessionBlocks([automationOpening, projectedReply], [rawReply]),
    [automationOpening, rawReply],
  )
})

test("accepted-message convergence keeps raw tool activity instead of replacing it with text only", () => {
  const timestamp = "2026-08-07T13:53:43.000Z"
  const projectedText: MessageBlock = {
    id: "assistant-turn",
    role: "assistant",
    parts: [{ type: "text", content: "Working on it" }],
    timestamp,
    metadata: { source: "session-transcript" },
  }
  const rawTurn: MessageBlock = {
    id: "assistant-turn",
    role: "assistant",
    parts: [
      { type: "tool_use", content: "", toolName: "Bash", toolInput: "{}" },
      { type: "tool_result", content: "done" },
      { type: "text", content: "Working on it" },
    ],
    timestamp,
  }
  const acceptedUser: MessageBlock = {
    id: "accepted-user",
    role: "user",
    parts: [{ type: "text", content: "One more thing" }],
    timestamp: "2026-08-07T13:54:00.000Z",
    metadata: { source: "user-message" },
  }

  assert.deepEqual(
    mergeDiscussionAndSessionBlocks([projectedText, acceptedUser], [rawTurn]),
    [rawTurn, acceptedUser],
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

test("revalidation reuses unchanged block identities instead of repainting history", () => {
  const existing = block("old", "old")
  const fetched = block("old", "old")

  const [merged] = mergeRevalidatedMessages([fetched], [existing], [existing])

  assert.equal(merged, existing)
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
