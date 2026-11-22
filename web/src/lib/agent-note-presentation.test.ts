import test from "node:test"
import assert from "node:assert/strict"
import type { ParsedEvent } from "@redbamboo/chat"
import { describeAgentNote, describeNote, describeSentNote, type NoteOriginDiscussion } from "./agent-note-presentation.ts"
import { buildEventPart } from "./message-order.ts"
import { resolveEventType } from "./event-type-resolution.ts"

const AGENTS: Record<string, { name: string; avatarUrl: string }> = {
  nova: { name: "Nova", avatarUrl: "/api/assets/nova.png" },
  "agent-guid": { name: "Nova", avatarUrl: "/api/assets/nova.png" },
  "leni-b863d153": { name: "Leni", avatarUrl: "/api/assets/leni.png" },
}
const DISCUSSIONS: Record<string, NoteOriginDiscussion> = {
  "94453f76": { title: "Taking initiative", agentId: "agent-guid", archived: false },
  a0000000: { title: "Old thread", agentId: "agent-guid", archived: true },
}
const describe = (over: Partial<Parameters<typeof describeNote>[0]>) => describeNote({
  agent: "nova",
  discussionId: "94453f76",
  topic: null,
  currentDiscussionId: "f6761850",
  resolveAgent: ref => AGENTS[ref],
  resolveDiscussion: id => DISCUSSIONS[id],
  ...over,
})

test("a note from an open discussion names the sender and opens the origin", () => {
  assert.deepEqual(describe({}), {
    name: "Nova",
    avatarUrl: "/api/assets/nova.png",
    origin: "Taking initiative",
    openDiscussionId: "94453f76",
  })
})

test("another Agent gets its own avatar and name", () => {
  const note = describe({ agent: "leni-b863d153" })
  assert.equal(note.name, "Leni")
  assert.equal(note.avatarUrl, "/api/assets/leni.png")
})

test("a bare-id note resolves its Agent through the discussion", () => {
  assert.equal(describe({ agent: null }).name, "Nova")
  assert.equal(describe({ agent: null, discussionId: "ffffffff" }).name, "An Agent")
})

test("an archived origin keeps its name but cannot be opened", () => {
  const note = describe({ discussionId: "a0000000" })
  assert.equal(note.origin, "Old thread")
  assert.equal(note.openDiscussionId, null)
})

test("an unknown or unreadable origin shows no hash and cannot be opened", () => {
  const unknown = describe({ discussionId: "ffffffff" })
  assert.equal(unknown.origin, "another discussion")
  assert.equal(unknown.openDiscussionId, null)
  assert.equal(describe({ discussionId: "ffffffff", topic: "agent-to-agent messages" }).origin, "agent-to-agent messages")
  assert.equal(describe({ discussionId: null }).origin, "another discussion")
})

test("a note an instance wrote to itself says so and links nowhere", () => {
  const note = describe({ discussionId: "f6761850", currentDiscussionId: "f6761850" })
  assert.equal(note.origin, "this discussion")
  assert.equal(note.openDiscussionId, null)
})

test("an Agent nobody can resolve never shows an id as its name", () => {
  assert.equal(describe({ agent: "00000000-0000-4000-8000-000000000000" }).name, "An Agent")
  assert.equal(describe({ agent: "takit" }).name, "Takit")
})

const sentTo = (over: Partial<Parameters<typeof describeSentNote>[0]>) => describeSentNote({
  targetDiscussionId: "94453f76",
  targetTitle: "Title when sent",
  targetAgentId: "agent-guid",
  resolveAgent: ref => AGENTS[ref],
  resolveDiscussion: id => DISCUSSIONS[id],
  ...over,
})

test("a sent note names who it went to, with the discussion's current title", () => {
  assert.deepEqual(sentTo({}), { name: "Nova", avatarUrl: "/api/assets/nova.png", target: "Taking initiative", openDiscussionId: "94453f76" })
})

test("a sent note to another Agent carries that Agent's name and avatar", () => {
  const to = sentTo({ targetAgentId: "leni-b863d153" })
  assert.equal(to.name, "Leni")
  assert.equal(to.avatarUrl, "/api/assets/leni.png")
})

test("a sent note keeps its stored title when the discussion is gone, and doesn't open it", () => {
  assert.deepEqual(sentTo({ targetDiscussionId: "ffffffff" }), { name: "Nova", avatarUrl: "/api/assets/nova.png", target: "Title when sent", openDiscussionId: null })
})

test("a sent note to an archived discussion can't be opened from the note", () => {
  const to = sentTo({ targetDiscussionId: "a0000000" })
  assert.equal(to.target, "Old thread")
  assert.equal(to.openDiscussionId, null)
})

test("a sent note to a confidential discussion names nothing", () => {
  assert.deepEqual(sentTo({ targetDiscussionId: null, targetTitle: null, targetAgentId: null }), { name: null, avatarUrl: null, target: "another discussion", openDiscussionId: null })
})

const resolve = (source: string) => resolveEventType(source, new Map())
const describePart = (source: string, content: string, data: Record<string, unknown> | null = null) => {
  const timestamp = "2026-11-04T15:00:00Z"
  const part = buildEventPart({ source, content, data, timestamp }, resolve)
  const event: ParsedEvent = { ...JSON.parse(part.toolInput!), key: part.toolName!.slice(6), text: part.content! }
  return { part, note: describeAgentNote(event, {
    currentDiscussionId: "f6761850",
    resolveAgent: ref => AGENTS[ref],
    resolveDiscussion: id => DISCUSSIONS[id],
    onOpenDiscussion: () => {},
  }) }
}

test("stored received and sent records keep their Markdown and open the appropriate discussion", () => {
  const received = describePart("event:nova-94453f76", "[internal] From 94453f76: **Build complete**")
  const sent = describePart("event:agent-note-sent", "**Rebuild now**", {
    targetDiscussionId: "94453f76", targetAgentId: "agent-guid",
  })
  assert.equal(received.part.toolName, "event:agent-note")
  assert.equal(received.part.content, "**Build complete**")
  assert.equal(received.note?.lead, "Note from Nova in")
  assert.equal(sent.part.toolName, "event:agent-note-sent")
  assert.equal(sent.part.content, "**Rebuild now**")
  assert.equal(sent.note?.lead, "Note to Nova in")
  for (const projection of [received, sent]) {
    assert.equal(projection.note?.discussion, "Taking initiative")
    assert.equal(projection.note?.discussionHref, "/apps/nova/chat/94453f76")
  }
})

test("the confidential target label has no name, avatar or link", () => {
  const { note } = describePart("event:agent-note-sent", "A note", {})
  assert.equal(note?.lead, "Note to")
  assert.equal(note?.discussion, "another discussion")
  assert.equal(note?.avatarUrl, null)
  assert.equal(note?.discussionHref, undefined)
  assert.equal(describePart("event:delegate", "Finished").note, undefined)
})
