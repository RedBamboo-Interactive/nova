import { test } from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import type { MessageBlock, MessagePart, PersistedMessage } from "@redbamboo/chat"
import { eventInputMessageUid } from "@redbamboo/chat/event-parts"
import { mergePagedDiscussionRecordsAndOverlays } from "./discussion-transcript.ts"
import { appendEvent, markEventFrameSources, orderMessages } from "./message-order.ts"

// A delivered event sits where the turn actually received it, at the time it was received, in
// the one merge both a live catch-up and a reload go through. These replay what really happened:
// the fixture holds real identities, order and times, with the message text elided.

const { rebuildBlocks } = await import("../../node_modules/@redbamboo/chat/src/lib/rebuild-blocks.ts")
const { placeQueuedEvents } = await import("../../node_modules/@redbamboo/chat/src/lib/message-queue.ts")

interface Overlay {
  id: string
  messageUid: string
  role: string
  timestamp: string
  senderAgentId?: string
  source: string
  parts: { type: string; content: string }[]
  stampedSender?: unknown
}
const fixture = JSON.parse(readFileSync(new URL("./event-delivery-replay.fixture.json", import.meta.url), "utf8")) as Record<
  "seq16743" | "batch16560", { records: PersistedMessage[]; overlays: Overlay[] }
>

const rebuild = (records: PersistedMessage[]) => markEventFrameSources(rebuildBlocks(records), records)

/** The shape toChatMessages gives an overlay. */
function overlayBlock(overlay: Overlay): MessageBlock {
  return {
    id: overlay.messageUid,
    role: overlay.role as MessageBlock["role"],
    parts: overlay.parts.map(part => ({ type: "text", content: part.content })),
    timestamp: overlay.timestamp,
    senderAgentId: overlay.senderAgentId,
    metadata: {
      source: overlay.source,
      messageUid: overlay.messageUid,
      ...(overlay.stampedSender ? { stampedSender: overlay.stampedSender } : {}),
    },
  }
}

function view(overlays: Overlay[] | MessageBlock[], records: PersistedMessage[]): MessageBlock[] {
  const blocks = (overlays as (Overlay | MessageBlock)[]).map(item => "source" in item ? overlayBlock(item) : item)
  return orderMessages(mergePagedDiscussionRecordsAndOverlays(blocks, records, rebuild))
}

const eventParts = (blocks: MessageBlock[]): MessagePart[] =>
  blocks.flatMap(block => block.parts.filter(part => eventInputMessageUid(part)))
const eventUids = (blocks: MessageBlock[]) => eventParts(blocks).map(part => eventInputMessageUid(part))
const positionOf = (blocks: MessageBlock[], uid: string) =>
  blocks.findIndex(block => block.parts.some(part => eventInputMessageUid(part) === uid))
const blockWithRecord = (blocks: MessageBlock[], messageUid: string) =>
  blocks.findIndex(block => block.id === messageUid || block.metadata?.messageUid === messageUid)
const timeOf = (timestamp: string | undefined) => new Date(timestamp ?? 0).getTime()
/** What the row and the modal read (parseEventPart, in a module node:test can't load). */
const parseEventPart = (part: MessagePart) => JSON.parse(part.toolInput ?? "{}") as { timestamp?: string; postedAt?: string }
const uidStarting = (prefix: string) => fixture.batch16560.records.find(record => record.messageUid?.startsWith(prefix))!.messageUid!

// ── Seq 16743: queued during a turn, delivered when the next turn took it ──────────────────────

const NOTE = "9100012e48eb4af78892019a63981488"

test("seq 16743: the note sits at the start of the turn that took it, not 75 minutes earlier", () => {
  const { records, overlays } = fixture.seq16743
  const blocks = view(overlays, records)

  assert.deepEqual(eventUids(blocks), [NOTE], "one row")
  assert.equal(blocks.filter(block => block.role === "user").length, 0, "the record draws no bubble")
  const note = positionOf(blocks, NOTE)
  assert.ok(note > blockWithRecord(blocks, "3519aa09108445dea65e3fab9082f301"), "after the reply it was queued behind")
  assert.ok(note < blockWithRecord(blocks, "08f541b900804af09c2957193b9a8325"), "before the turn it started")
})

test("seq 16743: the row's time is when it was processed, and the post time stays labelled", () => {
  const { records, overlays } = fixture.seq16743
  const part = eventParts(view(overlays, records))[0]!
  const event = parseEventPart(part)

  assert.equal(timeOf(event.timestamp), timeOf("2026-11-04T08:57:10.1068716+01:00"))
  assert.equal(timeOf(event.postedAt), timeOf("2026-11-04T07:42:41.8978556Z"))
  assert.equal(part.inputPlaced, true)
})

test("seq 16743: it keeps its sender and is never an empty bubble", () => {
  const { records, overlays } = fixture.seq16743
  const blocks = view(overlays, records)
  const part = eventParts(blocks)[0]!

  assert.equal(part.toolName, "event:agent-note")
  assert.deepEqual(blocks.filter(block => block.role === "user"), [])
  assert.ok(blocks.flatMap(block => block.parts).every(item => item.type !== "text" || item.content !== ""))
})

// ── Seqs 16560, 16562-16565: five events coalesced into one record ─────────────────────────────

const HEAD = "c65376512b50f9e6ef963ef37d5123be582f6d014d772cfcbf211e5c7703b6ff"

test("seqs 16560-16565: each event renders once, all at the batch's record, in posted order", () => {
  const { records, overlays } = fixture.batch16560
  const blocks = view(overlays, records)
  const members = overlays.map(overlay => overlay.messageUid)

  assert.deepEqual(eventUids(blocks), members)
  assert.equal(new Set(members.map(uid => positionOf(blocks, uid))).size, 1, "one group, at one place")
  assert.equal(blocks.filter(block => block.role === "user").length, 0, "no raw batched copy")
  const place = positionOf(blocks, HEAD)
  assert.ok(place > blockWithRecord(blocks, uidStarting("adb2e433")), "after what preceded the record")
  assert.ok(place < blockWithRecord(blocks, uidStarting("35ba8a31")), "before what followed it")
  for (const part of eventParts(blocks)) {
    assert.equal(timeOf(parseEventPart(part).timestamp), timeOf("2026-11-03T14:01:49.6454411+01:00"))
    assert.equal(part.inputPlaced, true)
  }
})

// ── Steered mid-turn, assistant text on both sides ─────────────────────────────────────────────

const assistant = (sequence: number, uid: string, timestamp: string, content: string): PersistedMessage => ({
  id: sequence, role: "assistant", eventType: "text", messageUid: uid, sequence, epoch: "e",
  timestamp, content,
})
const steeredRecord = (sequence: number, uids: string[], timestamp: string): PersistedMessage => ({
  id: sequence, role: "user", eventType: "text", messageUid: uids[0], sequence, epoch: "e", timestamp,
  content: uids.map(uid => `<nova-event source="agent-note" from-agent="Nova" from-agent-id="a1" from-discussion="d1">\n${uid}\n</nova-event>`).join("\n"),
  attachmentsJson: JSON.stringify({ inputMessageUids: uids, attachments: [], metadata: { novaEvent: true, senderAgentId: "a1" } }),
})
const note = (uid: string, timestamp: string): MessageBlock => ({
  id: uid, role: "user", parts: [{ type: "text", content: uid }], timestamp,
  metadata: { source: "event:agent-note", messageUid: uid, stampedSender: { agentId: "a1", discussionId: "d1", title: "Plans" } },
})

test("a steered note folds between the assistant text around it, one turn on both sides", () => {
  const records = [
    assistant(1, "turn", "2026-11-04T10:00:00.000Z", "Before."),
    steeredRecord(2, ["steer"], "2026-11-04T10:00:05.000Z"),
    assistant(3, "turn", "2026-11-04T10:00:06.000Z", "After."),
  ]
  // Posted before the turn's first words: its post time would put it above them.
  const blocks = view([note("steer", "2026-11-04T09:59:50.000Z")], records)

  assert.deepEqual(blocks.map(block => block.id), ["turn", "event-steer", "turn:segment:1"])
  assert.deepEqual(blocks.map(block => block.metadata?.messageUid ?? null), ["turn", null, "turn"], "one assistant turn")
})

test("live catch-up and reload give the same blocks for the same records", () => {
  const before = assistant(1, "turn", "2026-11-04T10:00:00.000Z", "Before.")
  const fold = steeredRecord(2, ["steer"], "2026-11-04T10:00:05.000Z")
  const after = assistant(3, "turn", "2026-11-04T10:00:06.000Z", "After.")
  const overlay = note("steer", "2026-11-04T09:59:50.000Z")

  // Waiting: no record yet, the event drawn at its post time, as the socket appended it.
  const waiting = view([overlay], [before])
  assert.equal(eventParts(waiting)[0]!.inputPlaced, undefined)
  // Then the catch-up page brings the fold and the rest. A reload reads the same two inputs.
  const caughtUp = view([overlay], [before, fold, after])
  const reloaded = view([overlay], [after, before, fold])

  assert.deepEqual(reloaded, caughtUp)
  assert.deepEqual(eventUids(caughtUp), ["steer"])
})

// ── The panel's half: waiting, then placed once ────────────────────────────────────────────────

function liveAppend(blocks: MessageBlock[], uid: string, timestamp: string): MessageBlock[] {
  return appendEvent(blocks, { messageUid: uid, source: "event:agent-note", content: uid, data: null, timestamp })
}
const queued = (uid: string, remoteState: string) => ({
  id: `q-${uid}`, content: uid, timestamp: Date.parse("2026-11-04T09:59:50.000Z"), messageUid: uid, internalEvent: true, remoteState,
}) as never

test("waiting then delivered: one row, one uid, right place, no queue copy", () => {
  const before = assistant(1, "turn", "2026-11-04T10:00:00.000Z", "Before.")
  const fold = steeredRecord(2, ["steer"], "2026-11-04T10:00:05.000Z")
  const after = assistant(3, "turn", "2026-11-04T10:00:06.000Z", "After.")
  const overlay = note("steer", "2026-11-04T09:59:50.000Z")

  const live = liveAppend(view([], [before]), "steer", overlay.timestamp)
  const waiting = placeQueuedEvents(live, [queued("steer", "pending")], true)
  assert.deepEqual(eventUids(waiting.messages), [], "not in the conversation while it waits")
  assert.equal(waiting.waiting.size, 1, "drawn with the queue")

  const delivering = placeQueuedEvents(live, [queued("steer", "delivered")], true)
  assert.equal(delivering.waiting.size, 1, "delivered with no record yet still waits, it moves once")

  const placed = placeQueuedEvents(view([overlay], [before, fold, after]), [queued("steer", "delivered")], true)
  assert.equal(placed.waiting.size, 0, "no queue copy")
  assert.deepEqual(eventUids(placed.messages), ["steer"], "one row, one uid")
  assert.equal(positionOf(placed.messages, "steer"), 1, "at its fold")
})

test("a socket event arriving after the snapshot placed it changes nothing", () => {
  const before = assistant(1, "turn", "2026-11-04T10:00:00.000Z", "Before.")
  const fold = steeredRecord(2, ["steer"], "2026-11-04T10:00:05.000Z")
  const placed = view([note("steer", "2026-11-04T09:59:50.000Z")], [before, fold])

  assert.equal(liveAppend(placed, "steer", "2026-11-04T09:59:50.000Z"), placed)
})

test("a steer demoted to the next turn lands at the start of that turn, once", () => {
  const records = [
    assistant(1, "first", "2026-11-04T10:00:00.000Z", "First turn."),
    { id: 2, role: "assistant", eventType: "status", content: "idle", sequence: 2, epoch: "e", timestamp: "2026-11-04T10:00:10.000Z" } as PersistedMessage,
    steeredRecord(3, ["demoted"], "2026-11-04T10:00:11.000Z"),
    assistant(4, "second", "2026-11-04T10:00:12.000Z", "Second turn."),
  ]
  const blocks = view([note("demoted", "2026-11-04T10:00:03.000Z")], records)

  assert.deepEqual(eventUids(blocks), ["demoted"])
  assert.ok(positionOf(blocks, "demoted") > blockWithRecord(blocks, "first"))
  assert.ok(positionOf(blocks, "demoted") < blockWithRecord(blocks, "second"))
})

test("a steer racing the turn's end: its record after the last words still holds the note", () => {
  const records = [
    assistant(1, "turn", "2026-11-04T10:00:00.000Z", "Last words."),
    steeredRecord(2, ["racer"], "2026-11-04T10:00:00.500Z"),
  ]
  const blocks = view([note("racer", "2026-11-04T09:59:59.000Z")], records)

  assert.deepEqual(blocks.map(block => block.id), ["turn", "event-racer"])
})

test("a keyed retry that admitted once places one row", () => {
  const records = [assistant(1, "turn", "2026-11-04T10:00:00.000Z", "A."), steeredRecord(2, ["keyed"], "2026-11-04T10:00:05.000Z")]
  const overlay = note("keyed", "2026-11-04T09:59:00.000Z")

  assert.deepEqual(eventUids(view([overlay, overlay], records)), ["keyed"])
})

test("a machine event places the same way and stays a dot", () => {
  const machine: MessageBlock = {
    id: "machine", role: "user", parts: [{ type: "text", content: "Build finished" }], timestamp: "2026-11-04T09:59:00.000Z",
    metadata: { source: "event:automation", messageUid: "machine" },
  }
  const record: PersistedMessage = {
    id: 2, role: "user", eventType: "text", messageUid: "machine", sequence: 2, epoch: "e", timestamp: "2026-11-04T10:00:05.000Z",
    content: `<nova-event source="automation">\nBuild finished\n</nova-event>`,
  }
  const blocks = view([machine], [assistant(1, "turn", "2026-11-04T10:00:00.000Z", "A."), record])
  const part = eventParts(blocks)[0]!

  assert.equal(part.toolName, "event:automation")
  assert.equal(positionOf(blocks, "machine"), 1)
  assert.equal(timeOf(parseEventPart(part).timestamp), timeOf("2026-11-04T10:00:05.000Z"))
})

test("an old event with no record and no queue item stays at its post time, visible", () => {
  const records = [assistant(1, "a", "2026-11-04T10:00:00.000Z", "A."), assistant(2, "b", "2026-11-04T10:00:10.000Z", "B.")]
  const old: MessageBlock = { ...note("old", "2026-11-04T10:00:05.000Z") }
  const blocks = view([old], records)
  const placed = placeQueuedEvents(blocks, [], false)

  assert.equal(placed.messages, blocks)
  assert.equal(positionOf(blocks, "old"), 1)
  const part = eventParts(blocks)[0]!
  assert.equal(part.inputPlaced, undefined)
  assert.equal(parseEventPart(part).postedAt, undefined)
  assert.equal(timeOf(parseEventPart(part).timestamp), timeOf("2026-11-04T10:00:05.000Z"))
})

test("a transcript record with no overlay loaded is already where it was received", () => {
  const blocks = view([], [assistant(1, "a", "2026-11-04T10:00:00.000Z", "A."), steeredRecord(2, ["bare"], "2026-11-04T10:00:05.000Z")])
  const part = eventParts(blocks)[0]!

  assert.equal(eventInputMessageUid(part), "bare")
  assert.equal(part.inputPlaced, true)
  // And its queue item can't lift it back out.
  assert.equal(placeQueuedEvents(blocks, [queued("bare", "delivered")], true).waiting.size, 0)
})

test("a person's own message is untouched by any of this", () => {
  const person: PersistedMessage = {
    id: 2, role: "user", eventType: "text", messageUid: "person", sequence: 2, epoch: "e", timestamp: "2026-11-04T10:00:05.000Z",
    content: "Hello", attachmentsJson: JSON.stringify({ inputMessageUids: ["person"], attachments: [], metadata: { app: "nova" } }),
  }
  const blocks = view([], [assistant(1, "a", "2026-11-04T10:00:00.000Z", "A."), person])
  const bubble = blocks.find(block => block.role === "user")!

  assert.equal(bubble.id, "person")
  assert.equal(bubble.parts[0]!.content, "Hello")
  assert.deepEqual(eventUids(blocks), [])
})
