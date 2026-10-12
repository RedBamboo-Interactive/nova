import { test } from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import {
  getInitialAgentIndex,
  orderAgentsByName,
  reconcileHighlightedAgentId,
  shouldAutoFocusNewDiscussionFilter,
} from "./new-discussion-picker.ts"
import type { AgentInfo } from "./types.ts"

const pickerSource = readFileSync(new URL("../components/new-discussion-picker.tsx", import.meta.url), "utf8")

function agent(id: string, name: string): AgentInfo {
  return {
    id,
    slug: name.toLowerCase(),
    name,
    description: null,
    avatarUrl: "",
  }
}

function ownerWindow(primaryPointerIsCoarse: boolean): Window {
  return {
    matchMedia: (query: string) => ({
      matches: query === "(pointer: coarse)" && primaryPointerIsCoarse,
    }),
  } as Window
}

test("orders agents alphabetically without mutating the API response", () => {
  const response = [agent("gemma", "Gemma"), agent("nova", "Nova"), agent("axl", "Axl")]

  const ordered = orderAgentsByName(response)

  assert.deepEqual(ordered.map(item => item.name), ["Axl", "Gemma", "Nova"])
  assert.deepEqual(response.map(item => item.name), ["Gemma", "Nova", "Axl"])
})

test("preselects the last used agent inside the alphabetical list", () => {
  const agents = orderAgentsByName([
    agent("gemma", "Gemma"),
    agent("nova", "Nova"),
    agent("axl", "Axl"),
  ])

  assert.equal(getInitialAgentIndex(agents, "nova"), 2)
  assert.equal(getInitialAgentIndex(agents, "missing"), 0)
  assert.equal(getInitialAgentIndex(agents, null), 0)
})

test("a background refresh preserves a valid selection made while loading", () => {
  const agents = orderAgentsByName([
    agent("gemma", "Gemma"),
    agent("nova", "Nova"),
    agent("axl", "Axl"),
  ])

  assert.equal(reconcileHighlightedAgentId(agents, "gemma", "nova"), "gemma")
  assert.equal(reconcileHighlightedAgentId(agents, "missing", "nova"), "nova")
  assert.equal(reconcileHighlightedAgentId(agents, null, "missing"), "axl")
})

test("autofocuses the Agent filter only for fine primary pointers", () => {
  assert.equal(shouldAutoFocusNewDiscussionFilter(ownerWindow(false)), true)
  assert.equal(shouldAutoFocusNewDiscussionFilter(ownerWindow(true)), false)
  assert.equal(shouldAutoFocusNewDiscussionFilter({} as Window), true)
})

test("the picker follows the command palette interior language", () => {
  assert.match(pickerSource, /<DialogHeader>/)
  assert.match(pickerSource, /<DialogDescription>/)
  assert.match(pickerSource, /data-slot="new-discussion-search"/)
  assert.match(pickerSource, /<DialogSearchField>/)
  assert.match(pickerSource, /initialFocus=\{autoFocusFilter \? undefined : dialogRef\}/)
  assert.match(pickerSource, /autoFocus=\{autoFocusFilter\}/)
  assert.match(pickerSource, /if \(open && agents\.length > 1 && autoFocusFilter\) filterRef\.current\?\.focus\(\)/)
  assert.match(pickerSource, /data-slot="new-discussion-agent-item"/)
  assert.match(pickerSource, /w-full rounded-lg px-3 py-2\.5/)
  assert.doesNotMatch(pickerSource, /border-b border-border-subtle last:border-b-0/)
  assert.doesNotMatch(pickerSource, /bg-surface-deep border border-border-subtle/)
})
