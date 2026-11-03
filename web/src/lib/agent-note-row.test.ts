import test from "node:test"
import assert from "node:assert/strict"
import type { MessagePart } from "@redbamboo/chat"
import { describeNote, isNoteRowPart, notePreview, type NoteOriginDiscussion } from "./agent-note-row.ts"
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

test("the preview is one plain line", () => {
  assert.equal(notePreview("**Bold** and `code`\n\n- item [link](http://x)\n## head"), "Bold and code item link head")
})

test("only a projected note row is lifted out of the frieze", () => {
  const resolve = (source: string) => resolveEventType(source, new Map())
  const part = (source: string, content: string) => buildEventPart({ source, content, data: null, timestamp: "2026-11-03T10:00:00Z" }, resolve)
  assert.equal(isNoteRowPart(part("event:nova-94453f76", "[internal] From 94453f76: hi")), true)
  assert.equal(isNoteRowPart(part("event:nova-94453f76", "no lead")), false, "keeps its named dot")
  assert.equal(isNoteRowPart(part("event:heartbeat", "[internal] x")), false)
  assert.equal(isNoteRowPart({ type: "tool_use", toolName: "event:agent-note", toolInput: "not json", content: "" } as MessagePart), false)
})
