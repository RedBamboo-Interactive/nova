import assert from "node:assert/strict"
import test from "node:test"
import { GoToMessageController, type GoToMessageView } from "./go-to-message.ts"

/** A transcript of `total` pages where the wanted message sits on page `target` counting back from the newest. */
function harness(options: { loadedPages: number; targetPage: number; totalPages: number; maxPages?: number }) {
  const timers: Array<() => void> = []
  const state = { loaded: options.loadedPages, loading: false, missing: 0, seeking: [] as boolean[], focused: [] as Array<{ uid: string; nonce: number }>, reads: 0 }
  const view: GoToMessageView = {
    hasEarlier: () => state.loaded < options.totalPages,
    isLoadingEarlier: () => state.loading,
    loadEarlier: async () => { state.reads++; state.loaded++ },
    focus: request => { state.focused.push(request) },
    missing: () => { state.missing++ },
    seeking: active => { state.seeking.push(active) },
  }
  const controller = new GoToMessageController(view, options.maxPages, callback => { timers.push(callback); return timers.length as unknown as ReturnType<typeof setTimeout> }, () => {})
  /** What the chat would answer to the latest request. */
  const answer = () => {
    const request = state.focused.at(-1)!
    controller.result({ uid: request.uid, nonce: request.nonce, found: state.loaded >= options.targetPage })
  }
  const flush = async () => {
    await Promise.resolve()
    await Promise.resolve()
    for (const callback of timers.splice(0)) callback()
  }
  return { controller, state, answer, flush }
}

test("a message already loaded is focused with one request and no paging", () => {
  const h = harness({ loadedPages: 1, targetPage: 1, totalPages: 10 })

  h.controller.go("uid-1")
  h.answer()

  assert.equal(h.state.reads, 0)
  assert.equal(h.controller.active, false)
  assert.deepEqual(h.state.seeking, [false])
  assert.equal(h.state.missing, 0)
})

test("an unloaded page is read earlier page by page until the chat finds the message", async () => {
  const h = harness({ loadedPages: 1, targetPage: 4, totalPages: 10 })

  h.controller.go("uid-1")
  for (let guard = 0; guard < 10 && h.controller.active; guard++) {
    h.answer()
    await h.flush()
  }

  assert.equal(h.state.reads, 3)
  assert.equal(h.controller.active, false)
  assert.equal(h.state.missing, 0)
  assert.deepEqual(h.state.seeking, [true, true, true, false])
  // Every retry is a fresh request, so the chat treats it as new work.
  assert.deepEqual(h.state.focused.map(request => request.nonce), [1, 2, 3, 4])
})

test("the search is bounded and then reports the message as gone", async () => {
  const h = harness({ loadedPages: 1, targetPage: 1000, totalPages: 1000, maxPages: 25 })

  h.controller.go("uid-1")
  for (let guard = 0; guard < 100 && h.controller.active; guard++) {
    h.answer()
    await h.flush()
  }

  assert.equal(h.state.reads, 25)
  assert.equal(h.state.missing, 1)
  assert.equal(h.controller.active, false)
})

test("history that runs out ends the search at once", async () => {
  const h = harness({ loadedPages: 3, targetPage: 99, totalPages: 3 })

  h.controller.go("uid-1")
  h.answer()

  assert.equal(h.state.reads, 0)
  assert.equal(h.state.missing, 1)
  assert.equal(h.controller.active, false)
})

test("an answer to an old request is ignored", () => {
  const h = harness({ loadedPages: 1, targetPage: 99, totalPages: 10 })

  h.controller.go("uid-1")
  h.controller.go("uid-2")
  h.controller.result({ uid: "uid-1", nonce: 1, found: false })

  assert.equal(h.state.reads, 0)
  assert.equal(h.controller.active, true)
})

test("cancelling stops a pending retry and clears the loading state", async () => {
  const h = harness({ loadedPages: 1, targetPage: 99, totalPages: 10 })

  h.controller.go("uid-1")
  h.answer()
  h.controller.cancel()
  await h.flush()

  assert.equal(h.state.focused.length, 1)
  assert.equal(h.state.seeking.at(-1), false)
  assert.equal(h.controller.active, false)
})

test("a retry waits while another earlier read is still running", async () => {
  const h = harness({ loadedPages: 1, targetPage: 2, totalPages: 10 })

  h.controller.go("uid-1")
  h.state.loading = true
  h.answer()
  await h.flush()
  assert.equal(h.state.focused.length, 1)

  h.state.loading = false
  await h.flush()
  assert.equal(h.state.focused.length, 2)
})
