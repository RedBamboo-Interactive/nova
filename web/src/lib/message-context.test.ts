import { test } from "node:test"
import assert from "node:assert/strict"
import type { MessageBlock } from "@redbamboo/chat"
import { accumulateMessageContexts, NOVA_CONTEXT_BLOCK_KEY, withMessageContexts } from "./message-context.ts"

const block = (id: string, role: MessageBlock["role"], metadata?: Record<string, unknown>): MessageBlock => ({
  id, role, parts: [{ type: "text", content: id }], timestamp: "2026-11-10T09:00:00.000Z", ...(metadata ? { metadata } : {}),
})

const context = (text: string) => ({ device: "Desktop", input: "text", novaContextBlock: text })

test("a page's contexts accumulate by uid and a later page replaces an entry", () => {
  const first = accumulateMessageContexts(new Map(), [
    { messageUid: "u1", metadata: context("one") },
    { messageUid: "u2", metadata: context("two") },
  ])
  const second = accumulateMessageContexts(first, [{ messageUid: "u2", metadata: context("two again") }])

  assert.deepEqual([...second.keys()], ["u1", "u2"])
  assert.equal(second.get("u2")?.novaContextBlock, "two again")
  assert.equal(first.get("u2")?.novaContextBlock, "two", "the earlier map is not mutated")
})

test("an older server without messageContexts, or malformed entries, change nothing", () => {
  const current = accumulateMessageContexts(new Map(), [{ messageUid: "u1", metadata: context("one") }])

  assert.equal(accumulateMessageContexts(current, undefined), current)
  assert.equal(accumulateMessageContexts(current, []), current)
  const next = accumulateMessageContexts(current, [
    null, { messageUid: "" , metadata: {} }, { messageUid: "u2" }, { messageUid: "u3", metadata: [] }, { metadata: {} },
  ])
  assert.deepEqual([...next.keys()], ["u1"])
})

test("each user block gets the context with its uid; its own metadata wins", () => {
  const contexts = accumulateMessageContexts(new Map(), [
    { messageUid: "u1", metadata: { ...context("one"), source: "user-message" } },
    { messageUid: "u2", metadata: context("two") },
  ])
  const blocks = [
    block("u1", "user", { source: "session-transcript", messageUid: "u1", internalInput: true }),
    block("turn-1", "assistant", { messageUid: "u2" }),       // an answer never takes a user's context
    block("u2", "user"),                                       // canonical block: id is the uid
    block("u3", "user", { messageUid: "u3" }),                 // recorded before contexts existed
  ]

  const stamped = withMessageContexts(blocks, contexts)

  assert.deepEqual(stamped[0].metadata, {
    device: "Desktop", input: "text", novaContextBlock: "one",
    source: "session-transcript", messageUid: "u1", internalInput: true,
  })
  assert.equal(stamped[1], blocks[1])
  assert.equal(stamped[2].metadata?.novaContextBlock, "two")
  // Identity, parts, order and time are what reconciliation reads; none of them moves.
  assert.deepEqual(stamped.map(b => [b.id, b.timestamp, b.parts]), blocks.map(b => [b.id, b.timestamp, b.parts]))
})

test("a user message with no stored context is marked as having none", () => {
  const blocks = [block("u3", "user", { messageUid: "u3" }), block("u4", "user"), block("a1", "assistant")]

  const stamped = withMessageContexts(blocks, new Map())

  assert.equal(NOVA_CONTEXT_BLOCK_KEY, "novaContextBlock")
  assert.deepEqual(stamped[0].metadata, { messageUid: "u3", novaContextBlock: null })
  assert.deepEqual(stamped[1].metadata, { novaContextBlock: null })
  assert.equal(stamped[2], blocks[2], "only user messages carry a context")
})

test("a block that already says what its context is, and a page without users, come back unchanged", () => {
  const known = [block("u1", "user", { novaContextBlock: "<nova-context>x</nova-context>" }), block("a1", "assistant")]
  const answers = [block("a1", "assistant")]

  assert.equal(withMessageContexts(known, new Map()), known)
  assert.equal(withMessageContexts(answers, new Map([["a1", context("x")]])), answers)
})
