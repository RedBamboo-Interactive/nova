import assert from "node:assert/strict"
import { test } from "node:test"
import { newDiscussionBody } from "./new-discussion-request.ts"

// createDiscussion's body before "Run on", verbatim (use-discussions.ts at ea1a97a).
function legacyBody(agentId?: string, qualityTier?: string, provider?: string, options?: { idempotencyKey?: string }) {
  const body: Record<string, string> = {}
  if (agentId) body.agentId = agentId
  if (qualityTier) body.qualityTier = qualityTier
  if (provider) body.provider = provider
  // A repeated request with the same key returns the same discussion instead of a second one.
  if (options?.idempotencyKey) body.idempotencyKey = options.idempotencyKey
  return Object.keys(body).length ? body : undefined
}

const cases: [string?, string?, string?, { idempotencyKey?: string }?][] = [
  [],
  ["agent-1"],
  ["agent-1", "standard"],
  ["agent-1", "deep", "codex"],
  ["agent-1", "deep", "codex", { idempotencyKey: "k-1" }],
  [undefined, undefined, undefined, { idempotencyKey: "k-2" }],
]

test("Automatic sends exactly the request it sent before nodes could be chosen", () => {
  for (const [agentId, tier, provider, options] of cases) {
    const before = JSON.stringify(legacyBody(agentId, tier, provider, options))
    assert.equal(JSON.stringify(newDiscussionBody(agentId, tier, provider, options)), before)
    assert.equal(JSON.stringify(newDiscussionBody(agentId, tier, provider, { ...options, nodeId: null })), before)
    assert.equal(JSON.stringify(newDiscussionBody(agentId, tier, provider, { ...options, nodeId: undefined })), before)
  }
  assert.equal(newDiscussionBody(), undefined, "no member at all is still no body")
})

test("a node picked in Run on adds nodeId after the existing members, and nothing else", () => {
  assert.equal(JSON.stringify(newDiscussionBody("agent-1", "deep", "codex", { idempotencyKey: "k-1", nodeId: "node_01STUDIO" })),
    '{"agentId":"agent-1","qualityTier":"deep","provider":"codex","idempotencyKey":"k-1","nodeId":"node_01STUDIO"}')
  assert.equal(JSON.stringify(newDiscussionBody(undefined, undefined, undefined, { nodeId: "node_01STUDIO" })),
    '{"nodeId":"node_01STUDIO"}')
})
