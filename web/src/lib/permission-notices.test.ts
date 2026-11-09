import { test } from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { addPermissionNotice, permissionNoticeText, readPermissionNotice } from "./permission-notices.ts"

test("a permission_denied event reads as a tool name and the reason", () => {
  assert.deepEqual(readPermissionNotice({ toolName: "Bash", content: "Nobody is watching this session." }),
    { toolName: "Bash", reason: "Nobody is watching this session." })
  assert.deepEqual(readPermissionNotice({}), { toolName: "A tool call", reason: "No reason was given." })
  assert.equal(permissionNoticeText({ toolName: "Bash", reason: "denied" }), "Bash was denied: denied")
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
