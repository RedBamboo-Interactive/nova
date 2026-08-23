import { test } from "node:test"
import assert from "node:assert/strict"
import {
  coalesceDiscussionTurnBlocks,
  filterInternalBootstrapBlock,
  mergeDiscussionAndSessionBlocks,
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

test("discussion projection keeps commentary and final answer in one canonical turn", () => {
  const commentary: MessageBlock = {
    id: "turn",
    role: "assistant",
    parts: [{ type: "text", content: "Working", phase: "commentary" }],
    timestamp: "2026-08-22T06:01:40.000Z",
    metadata: { messageUid: "turn", source: "session-transcript" },
  }
  const finalAnswer: MessageBlock = {
    ...commentary,
    parts: [{ type: "text", content: "Done", phase: "final_answer" }],
    timestamp: "2026-08-22T06:01:49.000Z",
  }

  assert.deepEqual(coalesceDiscussionTurnBlocks([commentary, finalAnswer]), [{
    ...commentary,
    parts: [...commentary.parts, ...finalAnswer.parts],
  }])
})

test("discussion projection gives interrupted segments stable unique identities", () => {
  const first: MessageBlock = {
    id: "turn",
    role: "assistant",
    parts: [{ type: "text", content: "Working", phase: "commentary" }],
    timestamp: "2026-08-22T06:01:40.000Z",
    metadata: { messageUid: "turn", source: "session-transcript" },
  }
  const ambient: MessageBlock = {
    id: "event",
    role: "user",
    parts: [{ type: "text", content: "ambient" }],
    timestamp: "2026-08-22T06:01:45.000Z",
    metadata: { source: "event:heartbeat" },
  }
  const continuation: MessageBlock = {
    ...first,
    parts: [{ type: "text", content: "Done", phase: "final_answer" }],
    timestamp: "2026-08-22T06:01:49.000Z",
  }

  const result = coalesceDiscussionTurnBlocks([first, ambient, continuation])
  assert.deepEqual(result.map(block => block.id), ["turn", "event", "turn:segment:1"])
  assert.equal(result[2].metadata?.messageUid, "turn")
})

test("removes only the internal Meet Nova bootstrap from raw session history", () => {
  const bootstrap: MessageBlock = {
    id: "setup-bootstrap",
    role: "user",
    parts: [{ type: "text", content: "internal setup instruction" }],
    timestamp: "2026-08-02T11:58:00.000Z",
  }

  assert.deepEqual(
    filterInternalBootstrapBlock([bootstrap, rawReply], "setup-bootstrap"),
    [rawReply],
  )
  assert.equal(filterInternalBootstrapBlock([rawReply], null)[0], rawReply)
})

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
