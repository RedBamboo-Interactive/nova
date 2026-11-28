import { test } from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { addPermissionNotice, PERMISSION_NOTICE_SUMMARY, readPermissionNotice } from "./permission-notices.ts"

test("a permission_denied event reads as a tool name and the reason", () => {
  assert.deepEqual(readPermissionNotice({ toolName: "Bash", content: "Nobody is watching this session." }),
    { toolName: "Bash", reason: "Nobody is watching this session." })
  assert.deepEqual(readPermissionNotice({}), { toolName: "A tool call", reason: "No reason was given." })
  assert.equal(readPermissionNotice({ content: "Denied", permission: { summary: "Get-Date" } }).action, "Get-Date")
  assert.equal(readPermissionNotice({ content: "Denied Get-Date", permission: { summary: "Get-Date" } }).action, undefined)
  assert.equal(readPermissionNotice({ permission: { summary: 5 } }).action, undefined)
})

test("the visible notice gives a next step and puts command output in collapsed details", () => {
  assert.match(PERMISSION_NOTICE_SUMMARY, /no one was available to approve it/)
  assert.match(PERMISSION_NOTICE_SUMMARY, /Send a message here while signed in/)
  const source = readFileSync(new URL("../panels/ChatView.tsx", import.meta.url), "utf8")
  const render = source.slice(source.indexOf("const renderPermissionNotices"), source.indexOf("const renderStatusLine"))
  assert.match(render, /<details [^>]*>\s*<summary[^>]*>[\s\S]*?\{PERMISSION_NOTICE_SUMMARY\}[\s\S]*?<\/summary>[\s\S]*?\{notice.reason\}/)
  assert.doesNotMatch(render, /<details[^>]* open[=> ]/)
})

test("notices keep the latest few and drop an identical repeat", () => {
  let list = addPermissionNotice(undefined, { toolName: "Bash", reason: "a" })
  list = addPermissionNotice(list, { toolName: "Bash", reason: "a" })
  assert.equal(list.length, 1)
  for (let i = 0; i < 8; i++) list = addPermissionNotice(list, { toolName: "Bash", reason: `r${i}` })
  assert.equal(list.length, 5)
  assert.equal(list.at(-1)?.reason, "r7")
})

test("the live event is shown beside the timeline and never enters the transcript accumulator", () => {
  const source = readFileSync(new URL("../hooks/use-discussions.ts", import.meta.url), "utf8")
  const denied = source.indexOf('(evt.type as string) === "permission_denied"')
  const projection = source.indexOf("const chatEvent: ChatEvent = {", denied)
  assert.ok(denied > 0 && projection > denied, "handled before the event is projected")
  assert.match(source.slice(denied, projection), /setPermissionNotices[\s\S]*return/)
})
