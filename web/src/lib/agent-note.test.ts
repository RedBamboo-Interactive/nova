import test from "node:test"
import assert from "node:assert/strict"
import { agentNoteLabel, classifyNote, parseAgentNote, parseAgentNoteSource, withoutInternalMarker } from "./agent-note.ts"

test("an instance source names the sending Agent and its discussion", () => {
  assert.deepEqual(parseAgentNoteSource("event:nova-855ddaca"), { agent: "nova", discussionId: "855ddaca" })
  assert.deepEqual(parseAgentNoteSource("Nova-855DDACA"), { agent: "nova", discussionId: "855ddaca" })
  assert.deepEqual(parseAgentNoteSource("event:leni-b863d153-855ddaca"), { agent: "leni-b863d153", discussionId: "855ddaca" })
})

test("registered and ordinary sources are not instance notes", () => {
  for (const source of ["event:camera-person", "event:release-manager", "event:heartbeat", "event:delegate", "event:nova-855ddac", "event:nova-855ddacaa"])
    assert.equal(parseAgentNoteSource(source), null, source)
})

test("the internal lead becomes the topic and leaves the note text", () => {
  const note = parseAgentNote(
    "event:nova-855ddaca",
    "[internal] From 855ddaca (Nightly / Sentinel vision): I am holding a coordinated FULL rebuild.",
  )
  assert.deepEqual(note, {
    agent: "nova",
    discussionId: "855ddaca",
    topic: "Nightly / Sentinel vision",
    text: "I am holding a coordinated FULL rebuild.",
  })
})

test("a note without the conventional lead keeps its text whole", () => {
  assert.equal(parseAgentNote("nova-79ed988c", "[internal] Leaf-capabilities is clean.")?.text, "Leaf-capabilities is clean.")
  assert.equal(parseAgentNote("nova-79ed988c", "Plain note")?.text, "Plain note")
  assert.equal(parseAgentNote("nova-79ed988c", "[internal] From 79ed988c: done")?.topic, null)
})

test("the label reads as the sender instance", () => {
  assert.equal(agentNoteLabel("nova", "855ddaca"), "Nova · 855ddaca")
})

test("notes are classified from the stored source and sender, never from text alone", () => {
  const text = "[internal] From 94453f76 (topic): hello"
  assert.deepEqual(classifyNote({ source: "event:nova-94453f76", text, registered: false }),
    { agent: "nova", discussionId: "94453f76", topic: "topic", text: "hello" })
  assert.deepEqual(classifyNote({ source: "event:b24a0794", text: "[internal] Reviewed.", registered: false }),
    { agent: null, discussionId: "b24a0794", topic: null, text: "Reviewed." })
  for (const source of ["event:nova-instance", "event:initiative", "event:josie-handoff", "event:system"])
    assert.equal(classifyNote({ source, text, registered: false }), null, source)
  assert.equal(classifyNote({ source: "event:nova-94453f76", text: "No lead here", registered: false }), null)
  assert.equal(classifyNote({ source: "event:b24a0794", text: "No lead here", registered: false }), null)
  assert.equal(classifyNote({ source: "event:feed-0123abcd", text, registered: true }), null)
})

test("the discussion activity type becomes a note only when its lead names that same discussion", () => {
  assert.deepEqual(classifyNote({ source: "event:discussion:e5e35835", text: "[internal] From e5e35835. Takit in DM.", registered: true }),
    { agent: null, discussionId: "e5e35835", topic: null, text: "Takit in DM." })
  assert.equal(classifyNote({ source: "event:discussion:e5e35835", text: "[internal] From 94453f76. Forged.", registered: true }), null)
  assert.equal(classifyNote({ source: "event:discussion:e5e35835", text: "Nova in \"title\": preview", registered: true }), null)
  assert.equal(classifyNote({ source: "event:discussion", text: "[internal] From e5e35835. Hi.", registered: true }), null)
})

test("a structured sender is a note unless its source is registered", () => {
  assert.deepEqual(classifyNote({ source: "event:agent-note", text: "Hi", senderAgentId: "id-1", senderDiscussionId: "94453f76", registered: false }),
    { agent: "id-1", discussionId: "94453f76", topic: null, text: "Hi" })
  assert.equal(classifyNote({ source: "event:delegate", text: "Hi", senderAgentId: "id-1", registered: true }), null)
})

test("the machine display drops only a leading internal marker", () => {
  assert.equal(withoutInternalMarker("[internal] From heartbeat: x"), "From heartbeat: x")
  assert.equal(withoutInternalMarker("Talking about [internal] leads"), "Talking about [internal] leads")
})
