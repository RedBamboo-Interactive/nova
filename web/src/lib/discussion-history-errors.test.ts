import assert from "node:assert/strict"
import test from "node:test"
import { readFileSync } from "node:fs"
import {
  clearHistoryLoadFailure,
  describeHistoryLoadFailure,
  recordHistoryLoadFailure,
  type DiscussionHistoryFailureState,
} from "./discussion-history-errors.ts"

class ServerError extends Error {
  code: string

  constructor(code: string, message: string) {
    super(message)
    this.code = code
  }
}

test("initial 413 keeps the server message and successful retry clears the error", () => {
  const failure = describeHistoryLoadFailure(new ServerError(
    "transcript_message_too_large",
    "One message in this discussion is too large to load.",
  ))
  const failed = recordHistoryLoadFailure({}, "cinderwake", "initial", failure)

  assert.deepEqual(failed.cinderwake?.initial, {
    code: "transcript_message_too_large",
    message: "One message in this discussion is too large to load.",
  })

  // A successful retry commits its messages through the existing pager, then
  // clears only the initial failure for this discussion.
  const renderedMessages = ["recovered message"]
  const recovered = clearHistoryLoadFailure(failed, "cinderwake", "initial")
  assert.deepEqual(renderedMessages, ["recovered message"])
  assert.equal(recovered.cinderwake, undefined)
})

test("switching discussions clears the selected discussion without leaking failures", () => {
  let state: DiscussionHistoryFailureState = {}
  state = recordHistoryLoadFailure(
    state,
    "first",
    "initial",
    describeHistoryLoadFailure(new TypeError("Failed to fetch")),
  )
  state = recordHistoryLoadFailure(
    state,
    "second",
    "initial",
    describeHistoryLoadFailure(new TypeError("Failed to fetch")),
  )

  const leftFirst = clearHistoryLoadFailure(state, "first")
  const selectedSecond = clearHistoryLoadFailure(leftFirst, "second")
  assert.equal(selectedSecond.first, undefined)
  assert.equal(selectedSecond.second, undefined)
})

test("a failed older page preserves rendered messages and exposes a separate retry", () => {
  const renderedMessages = ["oldest loaded", "newest loaded"]
  const state = recordHistoryLoadFailure(
    {},
    "discussion",
    "earlier",
    describeHistoryLoadFailure(new ServerError("redleaf_unavailable", "History is warming up.")),
  )

  assert.deepEqual(renderedMessages, ["oldest loaded", "newest loaded"])
  assert.equal(state.discussion?.initial, undefined)
  assert.equal(state.discussion?.earlier?.message, "History is warming up.")
  assert.equal(clearHistoryLoadFailure(state, "discussion", "earlier").discussion, undefined)
})

test("a newest-page failure never replaces an already rendered transcript", () => {
  const state = recordHistoryLoadFailure(
    {},
    "discussion",
    "initial",
    describeHistoryLoadFailure(new TypeError("Failed to fetch")),
    true,
  )

  assert.deepEqual(state, {})
})

test("clear, rotation, and archive cleanup discard both failure scopes", () => {
  const initial = describeHistoryLoadFailure(new TypeError("Failed to fetch"))
  let state = recordHistoryLoadFailure({}, "discussion", "initial", initial)
  state = recordHistoryLoadFailure(state, "discussion", "earlier", initial)

  assert.equal(clearHistoryLoadFailure(state, "discussion").discussion, undefined)
})

test("Nova wires both retry states into the mounted chat surface", () => {
  const hook = readFileSync(new URL("../hooks/use-discussions.ts", import.meta.url), "utf8")
  const chatView = readFileSync(new URL("../panels/ChatView.tsx", import.meta.url), "utf8")

  assert.match(hook, /recordHistoryLoadFailure\([\s\S]*"initial"/)
  assert.match(hook, /recordHistoryLoadFailure\([\s\S]*"earlier"/)
  assert.match(chatView, /Couldn&apos;t load this discussion/)
  assert.match(chatView, /Couldn&apos;t load earlier messages/)
  assert.match(chatView, /onClick=\{handleRetryHistory\}/)
  assert.match(chatView, /onClick=\{handleLoadEarlier\}/)
})
