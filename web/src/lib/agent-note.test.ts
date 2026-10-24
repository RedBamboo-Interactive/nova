import test from "node:test"
import assert from "node:assert/strict"
import { agentNoteLabel, parseAgentNote, parseAgentNoteSource } from "./agent-note.ts"

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
