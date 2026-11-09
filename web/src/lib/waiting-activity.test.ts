import { test } from "node:test"
import assert from "node:assert/strict"
import type { ActivityStatusGroup } from "@redbamboo/chat"
import { withWaitingActivity } from "./waiting-activity.ts"

const delegations: ActivityStatusGroup = {
  label: "1 delegation running...",
  items: [{ key: "s1", title: "Delegation", label: "worker", color: "red", running: true, renderDetails: () => null }],
}

test("nothing pending leaves the row's host work as it was", () => {
  assert.equal(withWaitingActivity(delegations, null), delegations)
  assert.equal(withWaitingActivity(null, undefined), null)
})

test("a parked approval says so in the row, even when the session is not streaming", () => {
  const group = withWaitingActivity(null, { kind: "permission" })
  assert.equal(group?.label, "Waiting for your approval...")
  assert.equal(group?.items[0].running, false)
})

test("a parked question says so, and delegations stay behind it", () => {
  const group = withWaitingActivity(delegations, {})
  assert.equal(group?.label, "Waiting for your answer...")
  assert.deepEqual(group?.items.map(item => item.key), ["pending-question", "s1"])
})
