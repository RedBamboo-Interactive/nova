import assert from "node:assert/strict"
import test from "node:test"
import {
  executionSummary,
  expectsPrompt,
  normalizeWorkflowGraph,
  promptAvailability,
  type AutomationDetailData,
} from "./automation-detail.ts"

test("executionSummary names the actor and user beneficiary", () => {
  const detail: AutomationDetailData = {
    id: "automation-1",
    ownership: {
      app: "nova",
      actor: { kind: "agent", id: "nova-id", name: "Nova" },
      beneficiary: { kind: "user", id: "user-id", name: "Laurent" },
    },
  }
  assert.equal(executionSummary(detail), "Nova runs this automation for Laurent.")
})

test("executionSummary makes system work explicit", () => {
  const detail: AutomationDetailData = {
    id: "automation-1",
    ownership: {
      app: "redleaf",
      actor: { kind: "application" },
      beneficiary: { kind: "system", reason: "No verifiable user owner" },
    },
  }
  assert.equal(executionSummary(detail), "RedLeaf runs this automation as system work.")
})

test("executionSummary does not disguise an unreviewed beneficiary as system work", () => {
  const detail: AutomationDetailData = {
    id: "automation-1",
    ownership: {
      app: "nova",
      actor: { kind: "agent", name: "Nova" },
      beneficiary: { kind: "unreviewed", reason: "Legacy fallback" },
    },
  }
  assert.equal(executionSummary(detail), "Nova has no authored beneficiary for this automation.")
})

test("prompt expectations only apply to AI-backed session actions", () => {
  assert.equal(expectsPrompt("nova-session"), true)
  assert.equal(expectsPrompt("ai-session"), true)
  assert.equal(expectsPrompt("http-check"), false)
  assert.equal(expectsPrompt("flow-execution"), false)
})

test("a failed detail request is unavailable, never a missing prompt", () => {
  assert.equal(promptAvailability("nova-session", null, false, true), "unavailable")
  assert.equal(promptAvailability("nova-session", null, false, false), "missing")
})

test("normalizeWorkflowGraph accepts legacy encoded graphs and rejects malformed members", () => {
  const graph = normalizeWorkflowGraph(JSON.stringify({
    nodes: [
      { id: "trigger", type: "trigger", position: { x: 10, y: 20 }, data: { label: "Every morning" } },
      { nope: true },
    ],
    edges: [
      { id: "edge-1", source: "trigger", target: "write" },
      { source: "broken" },
    ],
  }))
  assert.deepEqual(graph.nodes, [{
    id: "trigger",
    type: "trigger",
    position: { x: 10, y: 20 },
    data: { label: "Every morning", config: undefined },
  }])
  assert.deepEqual(graph.edges, [{ id: "edge-1", source: "trigger", target: "write" }])
})
