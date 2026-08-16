import { test } from "node:test"
import assert from "node:assert/strict"
import {
  getInitialAgentIndex,
  orderAgentsByName,
  reconcileHighlightedAgentId,
} from "./new-discussion-picker.ts"
import type { AgentInfo } from "./types.ts"

function agent(id: string, name: string): AgentInfo {
  return {
    id,
    slug: name.toLowerCase(),
    name,
    description: null,
    avatarUrl: "",
  }
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
