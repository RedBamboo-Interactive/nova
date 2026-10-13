import test from "node:test"
import assert from "node:assert/strict"
import {
  ScreenWakeLockLease,
  type ScreenWakeLockSentinelLike,
} from "./use-screen-wake-lock.ts"

function sentinel() {
  let released = false
  let releases = 0
  let onRelease: (() => void) | undefined
  const value: ScreenWakeLockSentinelLike = {
    get released() { return released },
    async release() {
      if (released) return
      released = true
      releases++
      onRelease?.()
    },
    addEventListener(_type, listener) { onRelease = listener },
  }
  return { value, releases: () => releases }
}

test("voice wake lock is acquired once and released when voice mode ends", async () => {
  const lease = new ScreenWakeLockLease()
  const held = sentinel()
  let requests = 0

  assert.equal(await lease.acquire(async type => {
    requests++
    assert.equal(type, "screen")
    return held.value
  }), true)
  assert.equal(await lease.acquire(async () => held.value), true)
  await lease.release()

  assert.equal(requests, 1)
  assert.equal(held.releases(), 1)
})

test("a wake-lock request rejection is a harmless unsupported-browser fallback", async () => {
  const lease = new ScreenWakeLockLease()

  assert.equal(await lease.acquire(async () => { throw new Error("NotAllowedError") }), false)
  await lease.release()
})

test("a late wake-lock grant is released after voice mode has already closed", async () => {
  const lease = new ScreenWakeLockLease()
  const late = sentinel()
  let resolveRequest!: (value: ScreenWakeLockSentinelLike) => void
  const acquisition = lease.acquire(() => new Promise(resolve => { resolveRequest = resolve }))

  await lease.release()
  resolveRequest(late.value)

  assert.equal(await acquisition, false)
  assert.equal(late.releases(), 1)
})
