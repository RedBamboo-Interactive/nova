import assert from "node:assert/strict"
import test from "node:test"
import { readFileSync } from "node:fs"
import {
  clearHistoryLoadFailure,
  describeHistoryLoadFailure,
  INVALID_HISTORY_PAGE_FAILURE,
  isInvalidHistoryPage,
  recordHistoryLoadFailure,
  type DiscussionHistoryFailureState,
} from "./discussion-history-errors.ts"
// The real shared pager, imported by file: its package entry also loads React components.
import { DurableTranscriptPager } from "../../node_modules/@redbamboo/chat/src/lib/durable-transcript-pager.ts"

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

test("a refresh failure over a loaded durable window stays quiet", () => {
  const state = recordHistoryLoadFailure(
    {},
    "discussion",
    "initial",
    describeHistoryLoadFailure(new TypeError("Failed to fetch")),
    true,
  )

  assert.deepEqual(state, {})
})

test("live-only blocks without a durable window still surface the newest-page failure", () => {
  // 2026-10-18 (3eab57ae): live stream blocks were on screen, the newest page
  // was rejected, and the discussion silently started mid-turn.
  const state = recordHistoryLoadFailure(
    {},
    "discussion",
    "initial",
    INVALID_HISTORY_PAGE_FAILURE,
    false,
  )

  assert.deepEqual(state.discussion?.initial, INVALID_HISTORY_PAGE_FAILURE)
})

test("only page-level pager rejections are invalid; superseded requests are not", () => {
  assert.equal(isInvalidHistoryPage("invalid-page"), true)
  assert.equal(isInvalidHistoryPage("incomplete-boundary"), true)
  for (const rejection of ["stale-request", "stale-anchor", "stale-epoch", undefined])
    assert.equal(isInvalidHistoryPage(rejection), false)
})

test("the exact production page with an inverted sequence edge is classified invalid", () => {
  // Session 1d600ab9e13b after-page: storage starts at 15692 but holds 15671.
  // The RedCompute regression advertised the storage edge as fromSequence.
  const sequences = [
    15692, 15671, 15689, 15691, 15711, 15713, 15714, 15746, 15748,
    15749, 15758, 15761, 15760, 15763, 15764, 15794, 15802, 15804,
  ]
  const records = sequences.map((sequence, index) => ({
    id: 24717201 + index,
    sessionId: "1d600ab9e13b",
    role: "assistant",
    eventType: "text",
    content: `record-${sequence}`,
    messageUid: "5dfd96d9",
    epoch: "epoch-1",
    sequence,
    timestamp: "2026-10-18T12:33:32Z",
  }))
  const page = (fromSequence: number) => ({
    epoch: "epoch-1",
    records,
    oldestCursor: "oldest",
    newestCursor: "newest",
    hasEarlier: true,
    hasLater: false,
    fromSequence,
    throughSequence: 15804,
    boundaryComplete: true,
  })

  const regressed = new DurableTranscriptPager()
  regressed.startNewestPage(1)
  const rejected = regressed.commitNewestPage(1, page(15692) as never)
  assert.equal(rejected.accepted, false)
  assert.equal(isInvalidHistoryPage(rejected.rejection), true)

  const repaired = new DurableTranscriptPager()
  repaired.startNewestPage(1)
  const accepted = repaired.commitNewestPage(1, page(15671) as never)
  assert.equal(accepted.accepted, true)
  assert.equal(accepted.hasEarlier, true)
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

test("Nova never drops an unverifiable history page silently", () => {
  const hook = readFileSync(new URL("../hooks/use-discussions.ts", import.meta.url), "utf8")
  const chatView = readFileSync(new URL("../panels/ChatView.tsx", import.meta.url), "utf8")

  assert.match(hook, /isInvalidHistoryPage\(result\.rejection\) \? "invalid" : "stale"/)
  assert.match(hook, /commit === "invalid"\) recordNewestFailure\(INVALID_HISTORY_PAGE_FAILURE\)/)
  assert.match(hook, /commit === "invalid"\) \{\s*\/\/[\s\S]*?resetHistoryPaging\(id\)\s*void loadMessages/)
  assert.match(hook, /"before", expectedCursor, runtimeRead\) === "invalid"\)[\s\S]*?"earlier",\s*INVALID_HISTORY_PAGE_FAILURE/)
  assert.match(chatView, /historyLoadFailure && activeMessages\.length > 0[\s\S]*?data-slot="history-partial-error"[\s\S]*?onClick=\{handleRetryHistory\}/)
})
