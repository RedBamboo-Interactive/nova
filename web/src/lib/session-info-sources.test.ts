import assert from "node:assert/strict"
import test from "node:test"
import { agentMemory, agentSkills, entityData, parseMood } from "./session-info-sources.ts"

test("entityData reads both an object and a JSON string, and nothing else", () => {
  assert.deepEqual(entityData({ data: { a: 1 } }), { a: 1 })
  assert.deepEqual(entityData({ data: "{\"a\":1}" }), { a: 1 })
  assert.deepEqual(entityData({ data: "[1]" }), {})
  assert.deepEqual(entityData({ data: "nope" }), {})
  assert.deepEqual(entityData(null), {})
})

test("agentSkills follows the agent's order and skips ids without an entity", () => {
  const agent = { id: "a", data: { skills: ["s2", "gone", "s1"] } }
  const skills = agentSkills(agent, [
    { id: "s1", name: "voice", slug: "voice", data: { icon: "ph-bold ph-waveform", description: "Generating voice" } },
    { id: "s2", name: "Chat Stability", slug: "chat-stability-6c146bad", data: "{\"description\":\"Protect state\"}" },
  ])
  assert.deepEqual(skills, [
    { id: "s2", name: "Chat Stability", slug: "chat-stability-6c146bad", icon: null, description: "Protect state" },
    { id: "s1", name: "voice", slug: "voice", icon: "ph-bold ph-waveform", description: "Generating voice" },
  ])
  assert.deepEqual(agentSkills({ id: "a", data: {} }, []), [])
})

const MOOD = `# Current Mood

Updated: 2026-11-23 03:32 UTC

## Emotional State

Energy: steady
Vibe: warm, playful
Color: brushed copper

## What I'm carrying

The Cranberries took us back to the year he laid out a report. I can still hear his dry "Indeed".

## Laurent's day

He filed the return.
`

test("mood: energy, vibe and colour name come from the file, with no swatch for a bare name", () => {
  assert.deepEqual(parseMood(MOOD), {
    energy: "steady",
    vibe: "warm, playful",
    colorName: "brushed copper",
    colorValue: null,
    note: "The Cranberries took us back to the year he laid out a report.",
  })
})

test("mood: a real hex on the colour line becomes the swatch and leaves the name", () => {
  const mood = parseMood("Energy: low\nColor: brushed copper (#B87333)\n")
  assert.equal(mood?.colorValue, "#B87333")
  assert.equal(mood?.colorName, "brushed copper")
})

test("mood: a file without the three lines, or no file, gives no mood", () => {
  assert.equal(parseMood("# Current Mood\n\nnothing here"), null)
  assert.equal(parseMood(null), null)
})

test("memory: the start list is the protocol's four notes", () => {
  const memory = agentMemory(null, "/workspace/x")
  assert.deepEqual(memory.readAtStart.map(note => note.path), [
    "memory/index.md", "memory/meta/feedback_style.md", "memory/dreaming/texture/texture.md", "memory/dreaming/mood.md",
  ])
  assert.equal(memory.workspaceHref, "/workspace/x")
})
