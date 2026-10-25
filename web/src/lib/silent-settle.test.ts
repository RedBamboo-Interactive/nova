import { test } from "node:test"
import assert from "node:assert/strict"
import type { MessageBlock, MessagePart } from "@redbamboo/chat"
import { hideSilentSettle, isSilentSettleText, NO_REPLY_MARKER } from "./silent-settle.ts"

function block(id: string, role: MessageBlock["role"], ...parts: MessagePart[]): MessageBlock {
  return { id, role, parts, timestamp: "2026-10-25T14:00:00.000Z", metadata: { messageUid: id } }
}

const text = (content: string): MessagePart => ({ type: "text", content })
const tool = (toolName: string): MessagePart => ({ type: "tool_use", content: "", toolName, toolInput: "{}" })

const question = block("user-1", "user", text("Did it ship?"))
const answer = block("turn-1", "assistant", text("Shipped."))

test("a settled silent turn leaves no assistant block", () => {
  const visible = hideSilentSettle([question, answer, block("turn-2", "assistant", text(`\n${NO_REPLY_MARKER}  `))])

  assert.deepEqual(visible.map(message => message.id), ["user-1", "turn-1"])
})

test("the marker never flashes while it streams in pieces", () => {
  // As the live accumulator grows the text part delta by delta.
  let streamed = ""
  for (const delta of ["<nova-", "no-re", "ply", "/>"]) {
    streamed += delta
    const visible = hideSilentSettle([question, block("turn-2", "assistant", text(streamed))])
    assert.deepEqual(visible.map(message => message.id), ["user-1"], `visible after ${JSON.stringify(streamed)}`)
  }
})

test("text that leaves the marker's prefix shows as written", () => {
  for (const content of ["<nova-no-reply/> but I do have something to say", "<b>bold</b>", "Hello"]) {
    assert.equal(isSilentSettleText(content), false, content)
    const shown = block("turn-2", "assistant", text(content))
    assert.equal(hideSilentSettle([shown])[0], shown)
  }
})

test("a reaction made during the silent turn keeps its tool squares", () => {
  const reacted = block("turn-2", "assistant", tool("Bash"), { type: "tool_result", content: "{\"success\":true}" }, text(NO_REPLY_MARKER))

  const [visible] = hideSilentSettle([reacted])

  assert.deepEqual(visible.parts.map(part => part.type), ["tool_use", "tool_result"])
})

test("a reaction note, live or from history, shows no bubble; a person's message and a context card stay", () => {
  const note = block("note-1", "user", text("<nova-context timestamp=\"2026-10-25T14:00:00Z\" input=\"reaction\" discussion=\"d1\">\nReactions since your last message:\n- Laurent reacted ❤️ to your latest message: \"Shipped.\"\n</nova-context>"))
  const typed = block("user-2", "user", text("<nova-context timestamp=\"x\">ctx</nova-context>\nThanks!"))
  const card = block("user-3", "user", text("<nova-context source=\"browser\"><app>Nova</app><url>/x</url></nova-context>"))

  const visible = hideSilentSettle([answer, note, typed, card])

  assert.deepEqual(visible.map(message => message.id), ["turn-1", "user-2", "user-3"])
})

test("nothing hidden returns the same array, so memoized consumers do not re-render", () => {
  const blocks = [question, answer]
  assert.equal(hideSilentSettle(blocks), blocks)
})
