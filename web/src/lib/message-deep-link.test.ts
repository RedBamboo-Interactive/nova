import { test } from "node:test"
import assert from "node:assert/strict"
import { GoToMessageController, type GoToMessageView } from "./go-to-message.ts"
import { messageDeepLinkStep, withoutMessageParam, type MessageDeepLinkState } from "./message-deep-link.ts"

function state(overrides: Partial<MessageDeepLinkState> = {}): MessageDeepLinkState {
  return { uid: "u1", discussionId: "d1", activeDiscussionId: "d1", loading: false, handled: null, ...overrides }
}

test("no parameter does nothing", () => {
  assert.deepEqual(messageDeepLinkStep(state({ uid: null })), { action: "none" })
  assert.deepEqual(messageDeepLinkStep(state({ discussionId: null })), { action: "none" })
})

test("it waits for the discussion to be selected and loaded, as an archived one takes a moment to be", () => {
  assert.deepEqual(messageDeepLinkStep(state({ activeDiscussionId: "other" })), { action: "wait" })
  assert.deepEqual(messageDeepLinkStep(state({ activeDiscussionId: null })), { action: "wait" })
  assert.deepEqual(messageDeepLinkStep(state({ loading: true })), { action: "wait" })
})

test("it goes once, and only again for a different message", () => {
  const step = messageDeepLinkStep(state())
  assert.equal(step.action, "go")
  if (step.action !== "go") return
  assert.equal(step.uid, "u1")
  assert.deepEqual(messageDeepLinkStep(state({ handled: step.handled })), { action: "none" })
  assert.equal(messageDeepLinkStep(state({ uid: "u2", handled: step.handled })).action, "go")
  assert.equal(messageDeepLinkStep(state({ discussionId: "d2", activeDiscussionId: "d2", handled: step.handled })).action, "go")
})

test("removing the parameter leaves the rest of the address alone", () => {
  assert.equal(withoutMessageParam("?message=u1"), "")
  assert.equal(withoutMessageParam("?agent=a1&message=u1&x=2"), "?agent=a1&x=2")
  assert.equal(withoutMessageParam(""), "")
})

function recordingView(answers: { hasEarlier: boolean }) {
  const calls: string[] = []
  const view: GoToMessageView = {
    hasEarlier: () => answers.hasEarlier,
    isLoadingEarlier: () => false,
    loadEarlier: () => { calls.push("earlier") },
    focus: (request) => calls.push(`focus:${request.uid}`),
    missing: () => calls.push("missing"),
    seeking: (active) => calls.push(`seeking:${active}`),
  }
  return { calls, view }
}

const immediately = (callback: () => void) => {
  queueMicrotask(callback)
  return 0 as unknown as ReturnType<typeof setTimeout>
}

test("a linked message that is found is focused through the existing controller", () => {
  const { calls, view } = recordingView({ hasEarlier: false })
  const controller = new GoToMessageController(view, 25, immediately, () => {})

  const step = messageDeepLinkStep(state())
  assert.equal(step.action, "go")
  if (step.action === "go") controller.go(step.uid)
  controller.result({ uid: "u1", nonce: controller.currentNonce, found: true })

  assert.ok(calls.includes("focus:u1"))
  assert.ok(!calls.includes("missing"))
})

test("a linked message that isn't there gets the existing missing feedback and nothing throws", () => {
  const { calls, view } = recordingView({ hasEarlier: false })
  const controller = new GoToMessageController(view, 25, immediately, () => {})

  controller.go("gone")
  assert.doesNotThrow(() => controller.result({ uid: "gone", nonce: controller.currentNonce, found: false }))

  assert.ok(calls.includes("focus:gone"))
  assert.equal(calls.filter((call) => call === "missing").length, 1)
})
