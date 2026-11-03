import test from "node:test"
import assert from "node:assert/strict"
import type { EventType } from "./types.ts"
import { humanizeEventSource, isUnregisteredEvent, resolveEventType } from "./event-type-resolution.ts"

test("delegation has the built-in magenta event presentation", () => {
  const event = resolveEventType("event:delegation", new Map())

  assert.equal(event.key, "delegation")
  assert.equal(event.color, "rgb(236 72 153)")
  assert.equal(event.icon, "ph-bold ph-code")
})

test("an authored event type can override the delegation presentation", () => {
  const authored: EventType = {
    key: "delegation",
    name: "Custom delegation",
    icon: "ph-bold ph-star",
    color: "#abc",
    description: null,
  }

  assert.equal(
    resolveEventType("event:delegation:session", new Map([["delegation", authored]])),
    authored,
  )
})

test("an Agent instance note resolves to the magenta agent-note presentation named after its sender", () => {
  const event = resolveEventType("event:nova-855ddaca", new Map())

  assert.equal(event.key, "agent-note")
  assert.equal(event.name, "Nova · 855ddaca")
  assert.equal(event.color, "rgb(236 72 153)")
})

test("a registered key wins over the instance-note pattern", () => {
  const authored: EventType = { key: "feed-0123abcd", name: "Feed", icon: null, color: "#abc", description: null }

  assert.equal(resolveEventType("event:feed-0123abcd", new Map([["feed-0123abcd", authored]])), authored)
})

test("the machine events history really holds are registered by name", () => {
  for (const [source, name] of [
    ["event:heartbeat", "Heartbeat"],
    ["event:heartbeat-tick", "Heartbeat tick"],
    ["event:morning-greeting", "Morning greeting"],
    ["event:spark", "Spark"],
    ["event:discord-reply", "Discord reply"],
    ["event:event-queue", "Event queue"],
  ] as const) {
    const event = resolveEventType(source, new Map())
    assert.equal(event.name, name)
    assert.ok(event.icon, source)
    assert.ok(event.color, source)
  }
})

test("an authored type overrides a new built-in", () => {
  const authored: EventType = { key: "heartbeat", name: "Pulse", icon: "ph-bold ph-star", color: "#abc", description: null }
  assert.equal(resolveEventType("event:heartbeat", new Map([["heartbeat", authored]])), authored)
})

test("an unregistered source is named after itself, never anonymous grey", () => {
  const event = resolveEventType("event:josie-handoff", new Map())
  assert.equal(isUnregisteredEvent(event), true)
  assert.equal(event.name, "Josie handoff")
  assert.notEqual(event.color, null)
  assert.notEqual(event.icon, "ph-bold ph-radio-button")
})

test("source names read as words, and a bare id is not echoed", () => {
  assert.equal(humanizeEventSource("josie-handoff"), "Josie handoff")
  assert.equal(humanizeEventSource("pixoo-reload-verification"), "Pixoo reload verification")
  assert.equal(humanizeEventSource("nova_instance"), "Nova instance")
  assert.equal(humanizeEventSource("initiative"), "Initiative")
  assert.equal(humanizeEventSource("b24a0794"), "From another discussion")
  assert.equal(humanizeEventSource(""), "Event")
})
