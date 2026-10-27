import assert from "node:assert/strict"
import test from "node:test"
import {
  NO_SESSION_EPOCH,
  NOVA_HIDDEN_ARTIFACT_ORIGINS,
  artifactsPath,
  createNovaArtifactsLoader,
  resolveNovaArtifactTarget,
} from "./artifacts-api.ts"

test("builds the discussion-scoped artifacts URL with only the parameters given", () => {
  assert.equal(artifactsPath("ab12", {}), "/api/apps/nova/discussions/ab12/artifacts")
  assert.equal(
    artifactsPath("ab12", { sinceSequence: 0, limit: 200, cursor: "a+b=", origins: ["user-text", "voice"] }),
    "/api/apps/nova/discussions/ab12/artifacts?origins=user-text%2Cvoice&sinceSequence=0&limit=200&cursor=a%2Bb%3D",
  )
  assert.equal(artifactsPath("a/b", {}), "/api/apps/nova/discussions/a%2Fb/artifacts")
})

test("the loader reads the Nova endpoint and passes a real page through untouched", async () => {
  const seen: string[] = []
  const page = { sessionId: "s", epoch: "e", throughSequence: 4, extractorVersion: 1, items: [], page: { nextCursor: null } }
  const load = createNovaArtifactsLoader("ab12", async <T,>(path: string) => { seen.push(path); return page as T })

  assert.deepEqual(await load({ sinceSequence: 3 }), page)
  assert.deepEqual(seen, ["/api/apps/nova/discussions/ab12/artifacts?sinceSequence=3"])
})

test("a discussion without a session loads as an empty page in a placeholder epoch", async () => {
  const empty = { sessionId: null, epoch: null, throughSequence: 0, extractorVersion: null, items: [], page: { nextCursor: null } }
  const load = createNovaArtifactsLoader("ab12", async <T,>() => empty as T)

  const page = await load({})

  assert.equal(page.epoch, NO_SESSION_EPOCH)
  assert.equal(page.sessionId, "")
  assert.equal(page.extractorVersion, 0)
  assert.deepEqual(page.items, [])
})

test("a refusal is thrown, never turned into an empty list", async () => {
  const load = createNovaArtifactsLoader("ab12", async () => { throw new Error("artifacts_unavailable") })

  await assert.rejects(() => load({}), /artifacts_unavailable/)
})

test("resolves targets the way Nova resolves attachments and proxied media", () => {
  const target = (value: string) => resolveNovaArtifactTarget({ target: value })

  assert.equal(target("/api/apps/nova/discussions/ab12/messages/7/output"), "/api/apps/nova/discussions/ab12/messages/7/output")
  assert.equal(target("/api/assets/a.png"), "/api/assets/a.png")
  assert.equal(target("/ai-session/input-attachments/abc"), "/ai-session/input-attachments/abc")
  assert.equal(target("https://example.com/a.png"), "https://example.com/a.png")
  assert.equal(target("redleaf://page/1"), null)
  assert.equal(target("C:/Projects/x/README.md"), null)
  assert.equal(target("//evil.example/api/x"), null)
  assert.equal(target("/ai-session/sessions/s/messages/7/output"), null)
})

test("tool-written files are hidden by default in Nova", () => {
  assert.deepEqual([...NOVA_HIDDEN_ARTIFACT_ORIGINS], ["tool-write"])
})
