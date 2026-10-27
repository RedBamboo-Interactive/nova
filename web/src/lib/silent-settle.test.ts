import { test } from "node:test"
import assert from "node:assert/strict"
import type { MessageBlock, MessagePart } from "@redbamboo/chat"
import { hideSilentSettle, isSilentSettleText, markInternalInputs, NO_REPLY_MARKER } from "./silent-settle.ts"

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

// Discussion ab7a1a07, 2026-10-27 07:35:33Z: a heart while Nova was mid-turn steered a reaction note into it.
// RedCompute keeps the envelope; Nova's history page (which the client reads to fill the steer's sequence gap,
// on settle and on reload) strips every <nova-context> from user text, so the note arrives as "".
const { rebuildBlocks } = await import("../../node_modules/@redbamboo/chat/src/lib/rebuild-blocks.ts")
const turn = "f830f7eed1e44752adeb2c7e3764623f"
const noteUid = "5719a81603344454a7abadfcf6c3b216"
const record = (sequence: number, role: string, eventType: string, extra: Record<string, unknown> = {}) => ({
  id: 27773200 + sequence - 3476, role, eventType, content: "", messageUid: turn,
  epoch: "11f733baaacbf45295dedb49b37f8f59", sequence, timestamp: `2026-10-27T07:35:${String(sequence - 3450).padStart(2, "0")}.000Z`, ...extra,
})
const steeredNote = record(3480, "user", "text", {
  messageUid: noteUid,
  attachmentsJson: JSON.stringify({
    inputMessageUids: [noteUid], attachments: [],
    metadata: { app: "nova", discussionId: "ab7a1a07", internalMessage: true, kind: "reaction-note" },
  }),
})
const historyPage = [
  record(3476, "assistant", "thinking", { content: "I should test reacting to his message." }),
  record(3478, "assistant", "tool_use", { toolName: "Bash", toolInput: "{}", messageId: "toolu_1" }),
  record(3479, "assistant", "tool_result", { toolResult: "{\"success\":true}", messageId: "toolu_1" }),
  steeredNote,
  record(3481, "assistant", "text", { content: "Your heart landed." }),
]

// What use-discussions does with every page of session records.
const project = (records: typeof historyPage) => hideSilentSettle(markInternalInputs(rebuildBlocks(records), records))

test("a reaction note steered mid-turn, as the history page returns it, shows no bubble", () => {
  const visible = project(historyPage)

  assert.equal(visible.filter(message => message.role === "user").length, 0)
  // The accepted shape: the hidden note still splits the running turn into two segments of one uid.
  assert.deepEqual(visible.map(message => message.id), [turn, `${turn}:segment:1`])
})

test("the same note read from raw session history, envelope intact, shows no bubble either", () => {
  const raw = { ...steeredNote, content: "<nova-context timestamp=\"2026-10-27T07:35:28Z\" input=\"reaction\" discussion=\"ab7a1a07\">\nReactions since your last message:\n- Laurent reacted ❤️ to your latest message\n</nova-context>" }

  assert.equal(project([...historyPage.slice(0, 3), raw, historyPage[4]]).filter(message => message.role === "user").length, 0)
})

test("internal metadata never hides what a person can see", () => {
  const internal = (sequence: number, content: string, attachments: Record<string, unknown> = {}) => record(sequence, "user", "text", {
    messageUid: `input-${sequence}`, content,
    attachmentsJson: JSON.stringify({ ...attachments, metadata: { app: "nova", internalMessage: true } }),
  })
  const records = [
    internal(3490, "<nova-event source=\"callback:session-complete\" type=\"session-complete\">Session worker done</nova-event>"),
    internal(3491, "Typed after the context"),
    internal(3492, "", { images: [{ mediaType: "image/png", base64: "AA==" }] }),
    internal(3493, "", { attachments: [{ id: "a", kind: "file", name: "f.txt", mediaType: "text/plain", size: 1, downloadUrl: "/f" }] }),
    // Not internal: Laurent's own message is never hidden on metadata grounds, even if it reached us empty.
    record(3494, "user", "text", { messageUid: "laurent-1", attachmentsJson: JSON.stringify({ metadata: { app: "nova", internalMessage: false } }) }),
  ]

  assert.deepEqual(project(records).map(message => message.id), ["input-3490", "input-3491", "input-3492", "input-3493", "laurent-1"])
})
