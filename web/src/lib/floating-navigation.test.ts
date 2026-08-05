import assert from "node:assert/strict"
import test from "node:test"
import { getFloatingNovaNavigationAction } from "./floating-navigation.ts"

const key = (
  value: string,
  modifiers: Partial<Pick<KeyboardEvent, "altKey" | "ctrlKey" | "metaKey" | "shiftKey" | "isComposing">> = {},
) => ({
  key: value,
  altKey: false,
  ctrlKey: false,
  metaKey: false,
  shiftKey: false,
  isComposing: false,
  ...modifiers,
})

test("maps Float Nova pane and discussion navigation shortcuts", () => {
  assert.equal(getFloatingNovaNavigationAction(key("ArrowLeft", { altKey: true })), "show-discussions")
  assert.equal(getFloatingNovaNavigationAction(key("ArrowRight", { altKey: true })), "show-chat")
  assert.equal(getFloatingNovaNavigationAction(key("ArrowDown", { altKey: true })), "next-discussion")
  assert.equal(getFloatingNovaNavigationAction(key("ArrowUp", { altKey: true })), "previous-discussion")
})

test("maps the Float-owned new discussion shortcut without colliding with browser new-window commands", () => {
  assert.equal(getFloatingNovaNavigationAction(key("n", { altKey: true })), "new-discussion")
  assert.equal(getFloatingNovaNavigationAction(key("N", { altKey: true })), "new-discussion")
  assert.equal(getFloatingNovaNavigationAction(key("n", { ctrlKey: true })), null)
  assert.equal(getFloatingNovaNavigationAction(key("N", { metaKey: true })), null)
})

test("rejects incomplete, conflicting, and composing shortcuts", () => {
  assert.equal(getFloatingNovaNavigationAction(key("ArrowDown")), null)
  assert.equal(getFloatingNovaNavigationAction(key("ArrowDown", { altKey: true, ctrlKey: true })), null)
  assert.equal(getFloatingNovaNavigationAction(key("n", { ctrlKey: true, altKey: true })), null)
  assert.equal(getFloatingNovaNavigationAction(key("ArrowLeft", { altKey: true, shiftKey: true })), null)
  assert.equal(getFloatingNovaNavigationAction(key("ArrowLeft", { altKey: true, isComposing: true })), null)
})
