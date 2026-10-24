import { test } from "node:test"
import assert from "node:assert/strict"
import type { MessageBlock, PersistedMessage } from "@redbamboo/chat"
import { eventInputMessageUid } from "@redbamboo/chat/event-parts"
import {
  mergeDiscussionAndSessionBlocks,
  mergePagedDiscussionAndSessionBlocks,
  mergePagedDiscussionRecordsAndOverlays,
} from "./discussion-transcript.ts"
import { appendEvent, orderMessages } from "./message-order.ts"

// RedCompute now coalesces queued events from one caller into one provider turn, exactly as it
// does ordinary messages: one canonical user record keyed by the first event's UID, listing every
// member in inputMessageUids. Each event must still render exactly once, from Nova's richer
// discussion overlay, and every member's queue item must find its marker.

const { rebuildBlocks } = await import("../../node_modules/@redbamboo/chat/src/lib/rebuild-blocks.ts")
const { canonicalUserMessageUids } = await import("../../node_modules/@redbamboo/chat/src/lib/message-queue.ts")

const members = ["event-one", "event-two", "event-three"]
const eventTime = (index: number) => `2026-10-24T15:32:5${index + 1}.000Z`

function frame(index: number): string {
  return `<nova-event source="callback:session-complete" type="session-complete" stopReason="unknown">\nSession worker-${index} completed\n</nova-event>`
}

/** The discussion copy EventInjector persists for each event (toChatMessages shape). */
function overlay(uid: string, index: number): MessageBlock {
  return {
    id: uid,
    role: "user",
    parts: [{ type: "text", content: frame(index) }],
    timestamp: eventTime(index),
    metadata: { source: `event:delegate:worker-${index}`, messageUid: uid, eventData: { outcome: "completed", index } },
  }
}

const overlays = members.map(overlay)
// What the provider persisted for the coalesced turn, and the reply it produced.
const batchedInput: PersistedMessage = {
  id: 40, role: "user", eventType: "text", messageUid: members[0], sequence: 40, epoch: "epoch",
  timestamp: "2026-10-24T15:34:00.000Z", content: members.map((_, index) => frame(index)).join("\n"),
  attachmentsJson: JSON.stringify({ inputMessageUids: members, attachments: [] }),
}
const reply: PersistedMessage = {
  id: 41, role: "assistant", eventType: "text", messageUid: "reply-turn", sequence: 41, epoch: "epoch",
  timestamp: "2026-10-24T15:34:05.000Z", content: "Acting on all three.", phase: "final_answer",
}

function eventUids(blocks: MessageBlock[]): string[] {
  return blocks.flatMap(block => block.parts.flatMap(part => eventInputMessageUid(part) ?? []))
}

function assertEachEventOnce(blocks: MessageBlock[], path: string) {
  assert.deepEqual(eventUids(blocks), members, `${path}: one marker per event, in order`)
  assert.equal(blocks.filter(block => block.role === "user").length, 0, `${path}: no raw batched copy`)
  const parts = blocks.flatMap(block => block.parts.filter(part => eventInputMessageUid(part)))
  // Each marker is the event's own projection, not a generic merged one.
  assert.deepEqual(parts.map(part => part.toolName), ["event:delegate", "event:delegate", "event:delegate"], path)
  assert.deepEqual(parts.map(part => part.content), members.map((_, index) => `Session worker-${index} completed`), path)
  const represented = canonicalUserMessageUids(blocks)
  for (const uid of members) assert.ok(represented.has(uid), `${path}: queue item ${uid} has its marker`)
  assert.equal(blocks.at(-1)?.parts.map(part => part.content).join(""), "Acting on all three.", path)
}

test("paged reload renders a batched event delivery once per event", () => {
  const merged = mergePagedDiscussionRecordsAndOverlays(overlays, [batchedInput, reply], rebuildBlocks)
  assertEachEventOnce(orderMessages(merged), "history-page")
})

test("legacy reload renders a batched event delivery once per event", () => {
  const merged = mergeDiscussionAndSessionBlocks(overlays, rebuildBlocks([batchedInput, reply]))
  assertEachEventOnce(orderMessages(merged), "legacy")
})

test("paged session-block merge renders a batched event delivery once per event", () => {
  const merged = mergePagedDiscussionAndSessionBlocks(overlays, rebuildBlocks([batchedInput, reply]))
  assertEachEventOnce(orderMessages(merged), "paged-session-blocks")
})

test("live pushes, a replayed push and a later reload converge on the same markers", () => {
  let live: MessageBlock[] = []
  members.forEach((uid, index) => {
    live = appendEvent(live, { messageUid: uid, source: `event:delegate:worker-${index}`, content: frame(index),
      data: { outcome: "completed", index }, timestamp: eventTime(index) })
  })
  // A duplicate socket delivery of one member is idempotent.
  live = appendEvent(live, { messageUid: members[1], source: "event:delegate:worker-1", content: frame(1),
    data: { outcome: "completed", index: 1 }, timestamp: eventTime(1) })
  assert.deepEqual(eventUids(live), members)
  const represented = canonicalUserMessageUids(live)
  for (const uid of members) assert.ok(represented.has(uid), `live: queue item ${uid} has its marker`)

  const reloaded = orderMessages(mergePagedDiscussionRecordsAndOverlays(overlays, [batchedInput, reply], rebuildBlocks))
  assert.deepEqual(eventUids(reloaded), eventUids(live))
})

test("a batched ordinary message delivery still replaces its user bridges", () => {
  const bridges: MessageBlock[] = ["m-one", "m-two"].map((uid, index) => ({
    id: uid, role: "user", parts: [{ type: "text", content: uid }], timestamp: eventTime(index),
    metadata: { source: "user-message", messageUid: uid },
  }))
  const record: PersistedMessage = { ...batchedInput, messageUid: "m-one", content: "m-one\nm-two",
    attachmentsJson: JSON.stringify({ inputMessageUids: ["m-one", "m-two"] }) }
  for (const merged of [
    mergeDiscussionAndSessionBlocks(bridges, rebuildBlocks([record])),
    mergePagedDiscussionRecordsAndOverlays(bridges, [record], rebuildBlocks),
  ]) {
    assert.deepEqual(merged.map(block => block.id), ["m-one"])
    assert.deepEqual(merged[0]?.inputMessageUids, ["m-one", "m-two"])
  }
})
