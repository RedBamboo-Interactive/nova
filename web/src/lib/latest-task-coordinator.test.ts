import assert from "node:assert/strict"
import test from "node:test"
import { LatestTaskCoordinator } from "./latest-task-coordinator.ts"

function deferred(): { promise: Promise<void>; resolve: () => void; reject: (error: Error) => void } {
  let resolve!: () => void
  let reject!: (error: Error) => void
  const promise = new Promise<void>((accept, fail) => {
    resolve = accept
    reject = fail
  })
  return { promise, resolve, reject }
}

test("coalesces an active task and keeps only the newest trailing task", async () => {
  const coordinator = new LatestTaskCoordinator<string>()
  const gate = deferred()
  const calls: string[] = []

  const active = coordinator.run("discussion-a", async () => {
    calls.push("active")
    await gate.promise
  })
  const superseded = coordinator.run("discussion-a", async () => { calls.push("superseded") })
  const trailing = coordinator.run("discussion-a", async () => { calls.push("trailing") })

  assert.equal(active, superseded)
  assert.equal(active, trailing)
  await Promise.resolve()
  assert.deepEqual(calls, ["active"])

  gate.resolve()
  await active
  assert.deepEqual(calls, ["active", "trailing"])
})

test("runs the newest trailing task after a failure and releases the key", async () => {
  const coordinator = new LatestTaskCoordinator<string>()
  const gate = deferred()
  const calls: string[] = []

  const active = coordinator.run("discussion-a", async () => {
    calls.push("failed")
    await gate.promise
  })
  coordinator.run("discussion-a", async () => { calls.push("recovery") })
  await Promise.resolve()
  gate.reject(new Error("snapshot failed"))

  await assert.rejects(active, /snapshot failed/)
  assert.deepEqual(calls, ["failed", "recovery"])

  await coordinator.run("discussion-a", async () => { calls.push("next") })
  assert.deepEqual(calls, ["failed", "recovery", "next"])
})

test("a passive duplicate cannot supersede a required trailing task", async () => {
  const coordinator = new LatestTaskCoordinator<string>()
  const gate = deferred()
  const calls: string[] = []

  const active = coordinator.run("discussion-a", async () => {
    calls.push("active")
    await gate.promise
  })
  coordinator.run("discussion-a", async () => { calls.push("required") })
  coordinator.run("discussion-a", async () => { calls.push("passive") }, false)

  await Promise.resolve()
  gate.resolve()
  await active
  assert.deepEqual(calls, ["active", "required"])
})
