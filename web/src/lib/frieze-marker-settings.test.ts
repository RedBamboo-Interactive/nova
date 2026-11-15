import { test } from "node:test"
import assert from "node:assert/strict"
import type { MessagePart } from "@redbamboo/chat"
import {
  activityMarkerRows,
  eventMarkerRows,
  friezeMarkerScope,
  friezeMarkerVisibility,
  normalizeHiddenFriezeMarkers,
  setFriezeMarkerShown,
} from "./frieze-marker-settings.ts"

const event = (key: string): MessagePart => ({ type: "tool_use", toolName: `event:${key}`, content: key })

test("Live has its own scope; every other discussion shares one", () => {
  assert.equal(friezeMarkerScope("live"), "live")
  assert.equal(friezeMarkerScope("chat"), "discussions")
  assert.equal(friezeMarkerScope("heartbeat"), "discussions")
  assert.equal(friezeMarkerScope(undefined), "discussions")
})

test("nothing hidden hands ChatPanel no predicate at all", () => {
  assert.equal(friezeMarkerVisibility([]), undefined)
})

test("the predicate hides exactly the chosen kinds", () => {
  const visible = friezeMarkerVisibility(["event:callback", "event:discussion"])!
  assert.equal(visible(event("callback")), false)
  assert.equal(visible(event("discussion")), false)
  assert.equal(visible(event("presence")), true)
  assert.equal(visible({ type: "tool_use", toolName: "Read", content: "" }), true)
  assert.equal(visible({ type: "tool_result", content: "" }), true)
})

test("toggling a scope leaves the other scope alone", () => {
  let state = normalizeHiddenFriezeMarkers(undefined)
  state = setFriezeMarkerShown(state, "live", "event:callback", false)
  state = setFriezeMarkerShown(state, "live", "event:callback", false)
  assert.deepEqual(state, { live: ["event:callback"], discussions: [] })
  state = setFriezeMarkerShown(state, "live", "event:callback", true)
  assert.deepEqual(state, { live: [], discussions: [] })
})

test("malformed storage reads as nothing hidden", () => {
  assert.deepEqual(normalizeHiddenFriezeMarkers({ live: "x", discussions: [1, "tool:shell"] }), { live: [], discussions: ["tool:shell"] })
  assert.deepEqual(normalizeHiddenFriezeMarkers(null), { live: [], discussions: [] })
})

test("event rows come from the API, plus built-ins it doesn't list, once each", () => {
  const rows = eventMarkerRows([
    { key: "callback", name: "Callback", icon: null, color: "#5B8BC4", description: null },
    { key: "delegate", name: "Delegate (registered)", icon: null, color: null, description: null },
    { key: "brand-new", name: "Brand new", icon: null, color: "#123456", description: null },
  ])
  const keys = rows.map(row => row.key)
  assert.ok(keys.includes("event:callback"))
  assert.ok(keys.includes("event:brand-new"))
  assert.equal(keys.filter(key => key === "event:delegate").length, 1)
  assert.equal(rows.find(row => row.key === "event:delegate")!.name, "Delegate (registered)")
  assert.ok(keys.includes("event:heartbeat"))
  assert.equal(rows.find(row => row.key === "event:delegation")!.shape, "square")
  assert.equal(rows.find(row => row.key === "event:callback")!.shape, "dot")
})

test("activity rows cover every tool category and thinking", () => {
  assert.deepEqual(activityMarkerRows().map(row => row.key), ["tool:read-only", "tool:mutating", "tool:shell", "tool:agent", "tool:other", "thinking"])
})
