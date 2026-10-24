import test from "node:test"
import assert from "node:assert/strict"
import type { EventType } from "./types.ts"
import { resolveEventType } from "./event-type-resolution.ts"

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
