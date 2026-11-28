import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import type { DiscussionInfo } from "../../lib/types.ts"

const behaviourSource = readFileSync(new URL("./discussion-behaviour.tsx", import.meta.url), "utf8")
const chatViewSource = readFileSync(new URL("../../panels/ChatView.tsx", import.meta.url), "utf8")
const webRoot = fileURLToPath(new URL("../../../", import.meta.url))

function discussion(overrides: Partial<DiscussionInfo> = {}): DiscussionInfo {
  return {
    id: "orbit", entityId: "orbit-entity", title: "Orbit", sessionId: "orbit-session", status: "idle", type: "chat",
    createdAt: "2026-11-28T09:00:00.000Z", lastActivity: "2026-11-28T09:01:00.000Z", messageCount: 3, agentId: "nova",
    orchestrated: true,
    ...overrides,
  }
}

const ongoing = discussion({ orchestration: { state: "ongoing", dueAt: "2026-11-28T09:04:00.000Z", chain: 2 } })
const settled = discussion({
  orchestration: { state: "settled", dueAt: null, chain: 2, settledReason: "complete", settledBy: "summary", settledAt: "2026-11-28T09:05:00.000Z" },
})

/** Renders the real components (TSX compiled by Vite, as in the bundle) to static markup. */
async function render(build: (components: Record<string, any>, h: typeof import("react").createElement) => unknown) {
  const { createServer } = await import("vite")
  const server = await createServer({
    root: webRoot, configFile: false, logLevel: "silent", appType: "custom",
    server: { middlewareMode: true, hmr: false, watch: null },
    optimizeDeps: { noDiscovery: true, include: [] },
    esbuild: { jsx: "automatic" },
    resolve: { dedupe: ["react", "react-dom"] },
    ssr: { external: ["react", "react-dom"] },
  })
  try {
    const components = await server.ssrLoadModule("/src/components/discussion/discussion-behaviour.tsx")
    const React = await import("react")
    const { renderToStaticMarkup } = await import("react-dom/server")
    return renderToStaticMarkup(build(components, React.createElement) as any)
  } finally {
    await server.close()
  }
}

test("an ongoing orchestration is a magenta heartbeat named ongoing", async () => {
  const html = await render(({ DiscussionBehaviourMarks }, h) => h(DiscussionBehaviourMarks, { discussion: ongoing, agentHasHeartbeat: true }))
  assert.match(html, /role="img"/)
  assert.match(html, /data-orchestration="ongoing"/)
  assert.match(html, /aria-label="Orchestrated · ongoing"/)
  assert.match(html, /title="Orchestrated · ongoing"/)
  assert.match(html, /class="[^"]*ph-heartbeat[^"]*text-domain-presence/)
  assert.doesNotMatch(html, /text-text-muted/)
  assert.doesNotMatch(html, /accent-red/)
})

test("a settled orchestration is a grey heartbeat, settled until you write", async () => {
  const html = await render(({ DiscussionBehaviourMarks }, h) => h(DiscussionBehaviourMarks, { discussion: settled, agentHasHeartbeat: true }))
  assert.match(html, /data-orchestration="settled"/)
  assert.match(html, /aria-label="Orchestrated · settled until you write"/)
  assert.match(html, /title="Orchestrated · settled until you write"/)
  assert.match(html, /class="[^"]*ph-heartbeat[^"]*text-text-muted opacity-60/)
  assert.doesNotMatch(html, /domain-presence/)
  assert.doesNotMatch(html, /accent-red/)
})

test("an older server's state, with no state field, reads as ongoing", async () => {
  const html = await render(({ DiscussionBehaviourMarks }, h) =>
    h(DiscussionBehaviourMarks, { discussion: discussion({ orchestration: { dueAt: null, chain: 0 } }), agentHasHeartbeat: true }))
  assert.match(html, /aria-label="Orchestrated · ongoing"/)
})

test("the Info modal's Orchestrated row says the same thing in words", async () => {
  const words = await render(({ OrchestrationStatus }, h) => h("div", null,
    h(OrchestrationStatus, { discussion: ongoing }),
    h(OrchestrationStatus, { discussion: { ...ongoing, status: "thinking", orchestration: { state: "ongoing", dueAt: null, chain: 1 } } }),
    h(OrchestrationStatus, { discussion: settled }),
    h(OrchestrationStatus, { discussion: { ...settled, orchestration: { ...settled.orchestration!, settledReason: "needs_you" } } }),
    h(OrchestrationStatus, { discussion: { ...settled, orchestration: { ...settled.orchestration!, settledReason: "heartbeat", settledNote: "Shipped; Laurent reviews it" } } }),
  ))
  assert.match(words, />Ongoing · the Heartbeat keeps it moving · 2 steps since you last wrote</)
  assert.match(words, />Ongoing · working · 1 step since you last wrote</)
  assert.match(words, />Settled until you write · done</)
  assert.match(words, />Settled until you write · needs you</)
  assert.match(words, />Settled until you write · Shipped; Laurent reviews it</)
  // The armed countdown and its red ping are gone.
  assert.doesNotMatch(words, /Heartbeat checks|animate-ping|accent-red/)
})

test("no red remains on the Orchestrated mark, its status or its Info row", () => {
  assert.doesNotMatch(behaviourSource, /accent-red|checks in soon|Heartbeat check due/)
  assert.match(behaviourSource, /export const ORCHESTRATION_ONGOING_COLOR = "var\(--color-domain-presence\)"/)
  assert.match(chatViewSource, /key: "orchestrated",[\s\S]{0,200}accent: orchestrationAccent\(activeDiscussion\)/)
})
