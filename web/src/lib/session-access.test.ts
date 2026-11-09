import { test } from "node:test"
import assert from "node:assert/strict"
import {
  AGENT_PERMISSIONS_SETTINGS_SECTION,
  FULL_ACCESS_SETTINGS_HREF,
  changeAccessLevel,
  currentOptionOnly,
  openSettingsFromLink,
  readAccessLevel,
  readAccessLevels,
  readFullAccessEnabled,
} from "./session-access.ts"

test("session info carries only levels the contract knows", () => {
  assert.equal(readAccessLevel("auto"), "auto")
  assert.equal(readAccessLevel("bypassPermissions"), null)
  assert.equal(readAccessLevel(null), null)
  assert.deepEqual(readAccessLevels(["plan", "ask", "acceptEdits", "full"]), ["plan", "ask", "full"])
  // None reported: the Info modal hides the Permissions row.
  assert.equal(readAccessLevels([]), null)
  assert.equal(readAccessLevels(undefined), null)
})

test("Full access is read from RedLeaf's policy, and an unreadable policy decides nothing", () => {
  assert.equal(readFullAccessEnabled({ version: 1, defaultLevel: "ask", agents: {}, fullAccess: { enabled: true, textVersion: 1 } }), true)
  assert.equal(readFullAccessEnabled({ fullAccess: { enabled: false } }), false)
  assert.equal(readFullAccessEnabled({ fullAccess: {} }), undefined)
  assert.equal(readFullAccessEnabled(null), undefined)
})

test("a change goes to the session's access-level route with the level", async () => {
  const calls: Array<{ path: string; body: unknown }> = []
  const post = async <T,>(path: string, body: unknown) => {
    calls.push({ path, body })
    return { accessLevel: "auto", ok: true } as T
  }
  assert.equal(await changeAccessLevel(post, "session/1", "auto"), "auto")
  assert.deepEqual(calls, [{ path: "/ai-session/sessions/session%2F1/access-level", body: { level: "auto" } }])
})

test("the server's refusal reaches the modal in its own words", async () => {
  const refusal = new Error("Raising a session's access level needs a person.")
  const post = async <T,>(): Promise<T> => { throw refusal }
  await assert.rejects(changeAccessLevel(post, "s", "full"), refusal)
})

test("a level the provider could not apply is a refusal, naming the level it stays at", async () => {
  const post = async <T,>() => ({ accessLevel: "ask", ok: false }) as T
  await assert.rejects(changeAccessLevel(post, "s", "auto"), /couldn't switch to Auto; it stays at Ask/)
})

test("the Full access link opens RedLeaf Settings on Agent permissions", () => {
  assert.equal(FULL_ACCESS_SETTINGS_HREF, "#settings=agent-permissions")
  const dispatched: Event[] = []
  const target = { dispatchEvent: (event: Event) => { dispatched.push(event); return true } }
  let prevented = false
  const link = { closest: (selector: string) => selector.includes("full-access-settings-link") ? {} : null }
  assert.equal(openSettingsFromLink({ target: link as unknown as EventTarget, preventDefault: () => { prevented = true } }, target), true)
  assert.equal(prevented, true)
  assert.equal(dispatched.length, 1)
  assert.equal(dispatched[0].type, "open-settings")
  assert.deepEqual((dispatched[0] as CustomEvent).detail, { section: AGENT_PERMISSIONS_SETTINGS_SECTION })
})

test("other clicks in the modal pass through untouched", () => {
  const dispatched: Event[] = []
  const target = { dispatchEvent: (event: Event) => { dispatched.push(event); return true } }
  let prevented = false
  const button = { closest: () => null }
  assert.equal(openSettingsFromLink({ target: button as unknown as EventTarget, preventDefault: () => { prevented = true } }, target), false)
  assert.equal(openSettingsFromLink({ target: null, preventDefault: () => { prevented = true } }, target), false)
  assert.equal(prevented, false)
  assert.equal(dispatched.length, 0)
})

test("Quality shows the session's own tier alone while Nova offers the access level", () => {
  const tiers = [{ value: "fast", label: "Fast" }, { value: "deep", label: "Deep", aliases: ["opus"] }]
  assert.deepEqual(currentOptionOnly(tiers, "deep"), [tiers[1]])
  assert.deepEqual(currentOptionOnly(tiers, "opus"), [tiers[1]])
  assert.equal(currentOptionOnly(tiers, "unknown"), undefined)
  assert.equal(currentOptionOnly(tiers, null), undefined)
})
