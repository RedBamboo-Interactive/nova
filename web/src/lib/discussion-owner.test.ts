import { test } from "node:test"
import assert from "node:assert/strict"
import { formatOwningApp } from "./discussion-owner.ts"

test("owning app ids render as product names", () => {
  assert.equal(formatOwningApp("storyboard"), "Storyboard")
})

test("a missing owner still names the discussion as owned elsewhere", () => {
  assert.equal(formatOwningApp(null), "another app")
  assert.equal(formatOwningApp("  "), "another app")
})
