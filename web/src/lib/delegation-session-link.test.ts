import test from "node:test"
import assert from "node:assert/strict"
import { delegationSessionPath } from "./delegation-session-link.ts"

test("builds a local Code route from a delegation event", () => {
  assert.equal(
    delegationSessionPath({ sessionId: "session/1" }),
    "/apps/codered/sessions/session%2F1",
  )
})

test("rejects delegation events without a usable session id", () => {
  assert.equal(delegationSessionPath(null), undefined)
  assert.equal(delegationSessionPath({}), undefined)
  assert.equal(delegationSessionPath({ sessionId: "" }), undefined)
  assert.equal(delegationSessionPath({ sessionId: 42 }), undefined)
})

test("a delegated-approval notice (key delegate) opens the worker session like the delegation marker", async () => {
  const { delegationEventPath } = await import("./delegation-session-link.ts")
  assert.equal(delegationEventPath("delegate", { sessionId: "w1" }), "/apps/codered/sessions/w1")
  assert.equal(delegationEventPath("delegation", { sessionId: "w1" }), "/apps/codered/sessions/w1")
  assert.equal(delegationEventPath("delegate", null), undefined)
  assert.equal(delegationEventPath("heartbeat", { sessionId: "w1" }), undefined)
})
