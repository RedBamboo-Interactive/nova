import assert from "node:assert/strict"
import { test } from "node:test"
import { runOnSessionNode } from "./run-on.ts"

const list = {
  nodes: [
    { id: "node_01ZERO", name: "WORKSTATION", isLocal: true },
    { id: "node_01STUDIO", name: "studio", isLocal: false },
  ],
  legacyNodeId: "node_01LEGACY",
}

test("the session's node is named from the node list", () => {
  assert.equal(runOnSessionNode(list, "node_01ZERO"), "WORKSTATION")
  assert.equal(runOnSessionNode(list, "node_01STUDIO"), "studio")
})

test("a session recorded before node ids names node zero by the legacy id", () => {
  assert.equal(runOnSessionNode(list, "node_01LEGACY"), "WORKSTATION")
  assert.equal(runOnSessionNode(list, "local"), "WORKSTATION")
})

test("nothing is guessed: no node, a node this person cannot see, or no list", () => {
  assert.equal(runOnSessionNode(list, null), null)
  assert.equal(runOnSessionNode(list, undefined), null)
  assert.equal(runOnSessionNode(list, "node_01SOMEONE_ELSES"), null)
  assert.equal(runOnSessionNode({}, "node_01ZERO"), null)
  assert.equal(runOnSessionNode({ nodes: [{ id: "node_01ZERO", isLocal: true }] }, "node_01ZERO"), null, "an unnamed node")
})
