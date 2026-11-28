import test from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { fileURLToPath } from "node:url"
import type { DiscussionSummary, DiscussionSummaryView } from "../../lib/discussion-summaries.ts"

const sidebarSource = readFileSync(new URL("./discussion-sidebar.tsx", import.meta.url), "utf8")
const summarySource = readFileSync(new URL("./discussion-summary.tsx", import.meta.url), "utf8")
const chatViewSource = readFileSync(new URL("../../panels/ChatView.tsx", import.meta.url), "utf8")
const webRoot = fileURLToPath(new URL("../../../", import.meta.url))

const needsYou: DiscussionSummary = {
  version: "v1",
  text: {
    status: "needs_you", label: "Sandbox launch approval", overview: "The eval is ready to launch in the Sandbox.",
    nextStep: "The eval runs once approved.", userAction: "Approve the Sandbox launch", waitingOn: "discussion:orbit-b",
  },
  source: "fast", updatedAt: "2026-11-28T09:04:00Z", sessionId: "s-a", basisActivity: "2026-11-28T09:00:00Z",
}
const resolvedView: DiscussionSummaryView = {
  discussionId: "a", enabled: true, override: null, version: "x", stale: false, summary: needsYou,
  via: { discussionId: "orbit-b", title: "Orbit", label: "Launch approval", userAction: "Approve the Sandbox launch", updatedAt: "2026-11-28T09:03:00Z" },
}

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
    const components = await server.ssrLoadModule("/src/components/discussion/discussion-summary.tsx")
    const React = await import("react")
    const { renderToStaticMarkup } = await import("react-dom/server")
    return renderToStaticMarkup(build(components, React.createElement) as any)
  } finally {
    await server.close()
  }
}

test("the list row shows an icon only, with the status as its accessible name", async () => {
  const html = await render(({ SummaryStatusIcon }, h) => h(SummaryStatusIcon, { status: "needs_you" }))
  assert.match(html, /role="img"/)
  assert.match(html, /aria-label="Needs you"/)
  assert.match(html, /title="Needs you"/)
  assert.match(html, /data-summary-status="needs_you"/)
  assert.match(html, /<i aria-hidden="true" class="ph-fill ph-hand-palm"><\/i>/)
  // No visible text: the label lives only in the accessible name and tooltip.
  assert.equal(html.replace(/<[^>]+>/g, "").trim(), "")
  // Not a dot: the unread dot is a filled circle span with no glyph.
  assert.doesNotMatch(html, /rounded-full/)

  // The row renders that icon in place of the old "· status · label" text.
  assert.match(sidebarSource, /\{summary && <SummaryStatusIcon status=\{summary\.text\.status\}/)
  assert.doesNotMatch(sidebarSource, /summary\.text\.label/)
  assert.doesNotMatch(sidebarSource, /status\.label/)
  assert.doesNotMatch(sidebarSource, /discussion-summary-label/)
  assert.match(sidebarSource, /const summary = currentDiscussionSummary\(discussion, summaryViews\?\.\[discussion\.id\]\)/)
})

test("inside a discussion a compact chip opens the full summary in a modal", async () => {
  const chip = await render(({ StatusChipButton }, h) => h(StatusChipButton, { status: "needs_you", expanded: false, onClick: () => {} }))
  assert.match(chip, /<button type="button" aria-haspopup="dialog" aria-expanded="false" aria-label="Status: Needs you\. Show summary"/)
  assert.equal(chip.replace(/<[^>]+>/g, "").trim(), "Needs you")
  assert.match(chip, /border-accent-gold-a30 bg-accent-gold\/10 text-accent-gold font-semibold/)
  assert.doesNotMatch(chip, /text-primary|domain-presence/)
  // Closed, the modal's content is not on the page.
  assert.doesNotMatch(chip, /Approve the Sandbox launch/)
  assert.doesNotMatch(chip, /The eval is ready/)

  const quiet = await render(({ StatusChipButton }, h) => h(StatusChipButton, { status: "waiting", expanded: true, onClick: () => {} }))
  assert.match(quiet, /aria-expanded="true"/)
  assert.match(quiet, /text-text-muted/)
  assert.doesNotMatch(quiet, /text-primary|accent-gold/)

  // Clicking opens the shared Dialog (base-ui; exercised in the browser) with the details below.
  assert.match(summarySource, /<StatusChipButton status=\{summary\.text\.status\} expanded=\{open\} onClick=\{\(\) => setOpen\(true\)\} \/>/)
  assert.match(summarySource, /<Dialog open=\{open\} onOpenChange=\{setOpen\}>/)
  assert.match(summarySource, /import \{ Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, cn \} from "@redbamboo\/ui"/)
  assert.match(summarySource, /w-\[calc\(100vw-2rem\)\] max-w-md/)

  const details = await render(({ DiscussionSummaryDetails }, h) => h(DiscussionSummaryDetails, {
    summary: needsYou, view: resolvedView, onOpenDiscussion: () => {},
  }))
  for (const text of ["What you need to do", "Approve the Sandbox launch", "Overview", "The eval is ready to launch in the Sandbox.",
    "What’s next", "The eval runs once approved.", "Updated", "Orbit"])
    assert.ok(details.includes(text), text)
  assert.match(details, /data-slot="discussion-summary-open-via"/)
  assert.match(details, /<time dateTime="2026-11-28T09:04:00Z">/)
  // The action block wears the same amber as the chip, never the brand or magenta.
  assert.match(details, /data-slot="discussion-summary-action" class="rounded-lg border border-accent-gold-a30 bg-accent-gold\/10 /)
  assert.match(details, /class="mb-1 text-xs font-semibold text-accent-gold">What you need to do/)
  assert.doesNotMatch(details, /text-primary|primary-a|domain-presence/)

  const complete = await render(({ DiscussionSummaryDetails }, h) => h(DiscussionSummaryDetails, {
    summary: { ...needsYou, text: { ...needsYou.text, status: "complete", userAction: null, waitingOn: null } },
  }))
  assert.doesNotMatch(complete, /What you need to do/)

  // ChatView shows the chip, not the old card.
  assert.match(chatViewSource, /<DiscussionStatusChip key=\{activeDiscussion\.id\} summary=\{activeSummary\} view=\{summaryView\}/)
  assert.match(chatViewSource, /onOpenDiscussion=\{handleSelectDiscussion\}/)
  assert.doesNotMatch(chatViewSource, /DiscussionSummaryCard/)
})
