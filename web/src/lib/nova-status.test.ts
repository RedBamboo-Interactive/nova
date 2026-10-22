import { test } from "node:test"
import assert from "node:assert/strict"
import { getNovaStreamingStatus } from "./nova-status.ts"
import type { MessageBlock, MessagePart } from "@redbamboo/chat"

const ts = "2026-10-22T09:00:00Z"
const turn = (...parts: MessagePart[]): MessageBlock => ({ id: "turn", role: "assistant", parts, timestamp: ts })
const tool = (toolName: string, input: Record<string, unknown> = {}, extra: Partial<MessagePart> = {}): MessagePart =>
  ({ type: "tool_use", toolName, toolInput: JSON.stringify(input), content: "", ...extra })

test("the label names the turn's latest activity", () => {
  assert.equal(getNovaStreamingStatus([]).label, "Responding...")
  assert.equal(getNovaStreamingStatus([turn({ type: "thinking", content: "" })]).label, "Thinking...")
  assert.equal(getNovaStreamingStatus([turn(tool("Read", { file_path: "a.ts" }))]).label, "Reading...")
  assert.equal(getNovaStreamingStatus([turn(tool("Read", { file_path: "memory/notes.md" }))]).label, "Remembering...")
  assert.equal(getNovaStreamingStatus([turn(tool("Bash", { command: "rg foo" }))]).label, "Searching...")
  assert.equal(getNovaStreamingStatus([turn(tool("Agent"))]).label, "Working...")
  assert.equal(getNovaStreamingStatus([turn({ type: "text", content: "Hi" })]).label, "Responding...")
})

test("ambient event blocks never set the label", () => {
  const ambient: MessageBlock = { id: "ev", role: "assistant", timestamp: ts,
    parts: [{ type: "tool_use", toolName: "event:weather", content: "" }] }
  assert.equal(getNovaStreamingStatus([turn({ type: "thinking", content: "" }), ambient]).label, "Thinking...")
})

// A background subagent reading files is not what Nova is doing in this turn.
test("background work never labels the turn", () => {
  const agent = tool("Agent", {}, { toolUseId: "toolu_agent", children: [tool("Read", { file_path: "a.ts" }, { parentToolUseId: "toolu_agent" })] })
  assert.equal(getNovaStreamingStatus([turn({ type: "text", content: "Launched" }), turn(agent)]).label, "Working...")
  const strayChild = tool("Read", { file_path: "a.ts" }, { parentToolUseId: "toolu_agent" })
  assert.equal(getNovaStreamingStatus([turn({ type: "thinking", content: "" }, strayChild)]).label, "Thinking...")
})
