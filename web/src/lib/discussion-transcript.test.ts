import { test } from "node:test"
import assert from "node:assert/strict"
import {
  coalesceDiscussionTurnBlocks,
  filterInternalBootstrapBlock,
  mergeDiscussionAndSessionBlocks,
  mergeNovaMessageArrival,
  mergePagedDiscussionRecordsAndOverlays,
  mergePagedDiscussionAndSessionBlocks,
} from "./discussion-transcript.ts"
import type { MessageBlock, PersistedMessage } from "@redbamboo/chat"

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

test("retains a persisted Agent audio card beside raw session history", () => {
  const voiceCard: MessageBlock = {
    id: "leni-voice-card",
    role: "assistant",
    parts: [
      { type: "text", content: "Es war schön, mit dir zu sprechen." },
      { type: "audio", content: "/api/assets/leni.mp3" },
    ],
    timestamp: "2026-08-24T20:44:35.426Z",
    senderAgentId: "leni-agent",
    metadata: { source: "nova-message" },
  }

  const merged = mergeDiscussionAndSessionBlocks(
    [projectedReply, voiceCard],
    [rawReply],
  )

  assert.deepEqual(merged, [rawReply, voiceCard])
  assert.equal(merged[1].senderAgentId, "leni-agent")
  assert.deepEqual(merged[1].parts[1], {
    type: "audio",
    content: "/api/assets/leni.mp3",
  })
})

test("canonical live Agent audio cards are stable and duplicate delivery is idempotent", () => {
  const arrival = {
    content: "This is actually me now.",
    audioUrl: "/api/assets/nova.mp3",
    senderAgentId: "nova-agent",
    messageUid: "voice-card",
    timestamp: "2026-08-24T20:34:28.039Z",
    fallbackId: "unused-fallback",
  }
  const once = mergeNovaMessageArrival([rawReply], arrival)

  assert.equal(once[1].id, "voice-card")
  assert.equal(once[1].senderAgentId, "nova-agent")
  assert.deepEqual(once[1].metadata, {
    source: "nova-message",
    messageUid: "voice-card",
  })
  assert.deepEqual(once[1].parts[1], {
    type: "audio",
    content: "/api/assets/nova.mp3",
  })
  assert.equal(mergeNovaMessageArrival(once, arrival), once)
})

test("legacy live Agent cards do not become duplicate-preserving overlays", () => {
  const result = mergeNovaMessageArrival([], {
    content: "Legacy card",
    audioUrl: "/api/assets/legacy.mp3",
    senderAgentId: "nova-agent",
    timestamp: "2026-08-24T20:34:28.039Z",
    fallbackId: "legacy-fallback",
  })

  assert.equal(result[0].id, "legacy-fallback")
  assert.equal(result[0].metadata, undefined)
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

function transcriptRecord(
  id: string,
  timestamp: string,
  content: string,
  sequence: number,
  overrides: Partial<PersistedMessage> = {},
): PersistedMessage {
  return {
    id,
    role: "assistant",
    eventType: "text",
    content,
    messageUid: "growing-turn",
    phase: "commentary",
    timestamp,
    epoch: "epoch-1",
    sequence,
    ...overrides,
  }
}

function delegationMarker(id: string, timestamp: string): MessageBlock {
  return {
    id,
    role: "assistant",
    parts: [{ type: "text", content: "delegation" }],
    timestamp,
    metadata: { source: "event:delegation", messageUid: id },
  }
}

function rebuildTestRecords(records: PersistedMessage[]): MessageBlock[] {
  return [...records]
    .sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0))
    .filter(record => record.eventType !== "status")
    .map(record => ({
    id: record.messageUid || `db-${record.id}`,
    role: record.role === "user" ? "user" : "assistant",
    parts: [{
      type: record.eventType as MessageBlock["parts"][number]["type"],
      content: record.content || record.toolResult || "",
      toolName: record.toolName ?? undefined,
      toolInput: record.toolInput ?? undefined,
      phase: record.phase ?? undefined,
    }],
    timestamp: record.timestamp,
    metadata: record.messageUid ? { messageUid: record.messageUid } : undefined,
  }))
}

test("paged event marker stays at its creation point as an assistant turn grows", () => {
  const before = transcriptRecord("record-1", "2026-09-12T12:00:00Z", "before", 1)
  const marker = delegationMarker("delegation-1", "2026-09-12T12:00:01Z")
  const after = transcriptRecord("record-2", "2026-09-12T12:00:02Z", "after", 2)
  const later = transcriptRecord("record-3", "2026-09-12T12:00:00.500Z", " later", 3)

  const initial = mergePagedDiscussionRecordsAndOverlays(
    [marker],
    [before, after],
    rebuildTestRecords,
  )
  const caughtUp = mergePagedDiscussionRecordsAndOverlays(
    [marker],
    [later, before, after],
    rebuildTestRecords,
  )

  assert.deepEqual(initial.map(block => block.id), [
    "growing-turn",
    "delegation-1",
    "growing-turn:segment:1",
  ])
  assert.deepEqual(caughtUp.map(block => block.id), [
    "growing-turn",
    "delegation-1",
    "growing-turn:segment:1",
  ])
  assert.equal(caughtUp[2]?.parts.map(part => part.content).join(""), "after later")
})

test("paged event overlay replaces its raw transcript copy by stable identity", () => {
  const marker = delegationMarker("delegation-1", "2026-09-12T12:00:01Z")
  const injectedCopy = transcriptRecord(
    "record-event",
    "2026-09-12T12:00:01Z",
    "plain injected copy",
    1,
    { messageUid: "delegation-1" },
  )

  assert.deepEqual(
    mergePagedDiscussionRecordsAndOverlays([marker], [injectedCopy], rebuildTestRecords),
    [marker],
  )
})


test("non-rendering status records do not suppress a durable overlay", () => {
  const overlay: MessageBlock = {
    ...delegationMarker("persisted-card", "2026-09-12T12:00:01Z"),
    metadata: { source: "nova-message", messageUid: "persisted-card" },
  }
  const status = transcriptRecord(
    "status-record",
    "2026-09-12T12:00:01Z",
    "",
    1,
    { eventType: "status", messageUid: "persisted-card" },
  )

  assert.deepEqual(
    mergePagedDiscussionRecordsAndOverlays([overlay], [status], rebuildTestRecords),
    [overlay],
  )
})

test("multiple delegation and completion markers remain distinct boundaries", () => {
  const first = transcriptRecord("record-1", "2026-09-12T12:00:00Z", "one", 1)
  const second = transcriptRecord("record-2", "2026-09-12T12:00:02Z", "two", 2)
  const third = transcriptRecord("record-3", "2026-09-12T12:00:04Z", "three", 3)
  const later = transcriptRecord("record-4", "2026-09-12T12:00:05Z", " four", 4)
  const delegated = delegationMarker("delegated", "2026-09-12T12:00:01Z")
  const completed: MessageBlock = {
    ...delegationMarker("completed", "2026-09-12T12:00:03Z"),
    metadata: { source: "event:delegate:session", messageUid: "completed" },
  }

  const initial = mergePagedDiscussionRecordsAndOverlays(
    [completed, delegated],
    [third, first, second],
    rebuildTestRecords,
  )
  const caughtUp = mergePagedDiscussionRecordsAndOverlays(
    [completed, delegated],
    [later, third, first, second],
    rebuildTestRecords,
  )
  const expectedIds = [
    "growing-turn",
    "delegated",
    "growing-turn:segment:1",
    "completed",
    "growing-turn:segment:2",
  ]

  assert.deepEqual(initial.map(block => block.id), expectedIds)
  assert.deepEqual(caughtUp.map(block => block.id), expectedIds)
  assert.equal(caughtUp[4]?.parts.map(part => part.content).join(""), "three four")
})
test("paged boundaries preserve attachments, context, and tool records", () => {
  const contextRecord = transcriptRecord(
    "user-context",
    "2026-09-12T12:00:00Z",
    '<nova-context timestamp="2026-09-12T12:00:00Z">context</nova-context>Question',
    1,
    {
      role: "user",
      messageUid: "user-turn",
      attachmentsJson: JSON.stringify({
        images: [{ mediaType: "image/png", base64: "aW1hZ2U=" }],
        attachments: [{
          id: "attachment-1",
          kind: "file",
          name: "proof.txt",
          mediaType: "text/plain",
          size: 5,
          downloadUrl: "/api/attachments/attachment-1",
        }],
      }),
    },
  )
  const toolUse = transcriptRecord(
    "tool-use",
    "2026-09-12T12:00:01Z",
    "",
    2,
    {
      eventType: "tool_use",
      toolName: "delegate",
      toolInput: "{}",
    },
  )
  const marker = delegationMarker("delegation-1", "2026-09-12T12:00:02Z")
  const toolResult = transcriptRecord(
    "tool-result",
    "2026-09-12T12:00:03Z",
    "complete",
    3,
    {
      eventType: "tool_result",
      toolResult: "complete",
    },
  )

  const rebuiltSpans: PersistedMessage[][] = []
  const result = mergePagedDiscussionRecordsAndOverlays(
    [marker],
    [toolResult, contextRecord, toolUse],
    records => {
      if (records.some(record => record.role !== "user")) rebuiltSpans.push(records)
      return rebuildTestRecords(records)
    },
  )

  assert.deepEqual(result.map(block => block.id), [
    "user-turn",
    "growing-turn",
    "delegation-1",
    "growing-turn:segment:1",
  ])
  const rebuiltRecords = rebuiltSpans.flat()
  assert.deepEqual(rebuiltRecords.map(record => record.id), [
    "user-context",
    "tool-use",
    "tool-result",
  ])
  assert.match(rebuiltRecords[0]?.content ?? "", /<nova-context/)
  assert.equal(JSON.parse(rebuiltRecords[0]?.attachmentsJson ?? "{}").images[0].base64, "aW1hZ2U=")
  assert.equal(JSON.parse(rebuiltRecords[0]?.attachmentsJson ?? "{}").attachments[0].name, "proof.txt")
  assert.equal(result[1]?.parts[0]?.type, "tool_use")
  assert.equal(result[3]?.parts[0]?.type, "tool_result")
})

test("canonical batches replace original user bridges but preserve unrelated same-text inputs", () => {
  const canonical: MessageBlock = { id: "first", role: "user", timestamp: "2026-09-12T22:00:01Z",
    inputMessageUids: ["first", "second"], parts: [{ type: "text", content: "one\ntwo" }] }
  const bridge: MessageBlock = { id: "second", role: "user", timestamp: "2026-09-12T22:00:00Z",
    metadata: { source: "user-message", messageUid: "second" }, parts: [{ type: "text", content: "two" }] }
  const unrelated: MessageBlock = { ...bridge, id: "third", metadata: { source: "user-message", messageUid: "third" } }
  assert.deepEqual(mergePagedDiscussionAndSessionBlocks([bridge, unrelated], [canonical]), [unrelated, canonical])
  assert.deepEqual(mergeDiscussionAndSessionBlocks([bridge, unrelated], [canonical]), [unrelated, canonical])
  const record: PersistedMessage = { id: 1, role: "user", eventType: "text", messageUid: "first",
    timestamp: canonical.timestamp, content: "one\ntwo", attachmentsJson: JSON.stringify({ inputMessageUids: ["first", "second"] }) }
  assert.deepEqual(mergePagedDiscussionRecordsAndOverlays([bridge, unrelated], [record], records => records.map(r => ({
    ...canonical, inputMessageUids: JSON.parse(r.attachmentsJson!).inputMessageUids,
  }))), [unrelated, canonical])
})
