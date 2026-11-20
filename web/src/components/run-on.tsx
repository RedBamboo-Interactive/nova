import { useEffect, useState } from "react"
import { NodeIcon, RunOnChoice, fetchRunOnNodes, parseRunOnNodes, runOnCompactLabel, runOnView } from "@redbamboo/ui"
import { runOnSessionNode, type NodeListBody } from "../lib/run-on"

/**
 * @redbamboo/ui is the kernel's shared copy: a kernel built before RunOnChoice has none of these exports. Nova then
 * shows no "Run on" anywhere and discussions run automatically, sending exactly today's requests.
 */
export const runOnAvailable = !!RunOnChoice && !!fetchRunOnNodes && !!parseRunOnNodes && !!runOnView

/**
 * The compact, icon-only RunOnChoice came with the node icons: a shared UI with RunOnChoice but not those would
 * draw the full-width row on the footer line. Without it the footer has no Run on and discussions run
 * automatically, as above.
 */
export const runOnCompactAvailable = runOnAvailable && !!runOnCompactLabel && !!NodeIcon

/** The work a discussion's sessions do: nodes without AI sessions are listed disabled. */
export const AI_SESSION = { slug: "ai-session", name: "AI sessions" }

/** The compact trigger beside Provider and Tier: their height, their muted hover. */
export const RUN_ON_COMPACT = "[&_[data-slot=run-on-choice-trigger]]:h-[26px] [&_[data-slot=run-on-choice-trigger]]:hover:bg-overlay-5"

/**
 * The discussion settings row (Phase 2 design 3.2), laid out like its neighbours (Confidential, LIVE timeline):
 * "Running on <the session's node>" once a session exists, and on the right the compact Run on for new
 * sessions, the node's icon with its name in the tooltip. With two or more nodes that can run sessions it opens
 * the list, Automatic first, and the text says the current session stays where it is; with one it is the
 * node's icon alone. Without the compact mode (an older shared UI) the row keeps its text only.
 */
export function DiscussionRunOnSetting({ nodeId, sessionNodeId, hasSession, disabled, onChange }: {
  /** The discussion's choice for new sessions; null is Automatic. */
  nodeId: string | null
  /** The node the current session runs on, from the session; null when unknown. */
  sessionNodeId: string | null
  hasSession: boolean
  disabled?: boolean
  onChange: (nodeId: string | null) => void
}) {
  const [list, setList] = useState<NodeListBody | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    fetchRunOnNodes(controller.signal)
      .then(body => { if (!controller.signal.aborted) setList(body as NodeListBody) })
      .catch(() => { /* no list: the row says nothing it cannot back */ })
    return () => controller.abort()
  }, [])

  let nodes: ReturnType<typeof parseRunOnNodes> = []
  try { nodes = list ? parseRunOnNodes(list) : [] } catch { nodes = [] }
  const view = list ? runOnView(nodes, AI_SESSION, nodeId) : null
  const running = list ? runOnSessionNode(list, sessionNodeId) : null
  const picker = view?.mode === "picker"

  const choosing = picker && runOnCompactAvailable
  if (!running && !choosing) return null
  const current = running && hasSession

  return (
    <div data-setting-path="discussion.runOn"
      className="flex items-start justify-between gap-3 rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2.5">
      <div className="flex min-w-0 items-start gap-2.5">
        <i className="ph-bold ph-desktop-tower mt-0.5 text-sm text-text-muted" aria-hidden="true" />
        <div className="min-w-0">
          <div className="text-xs font-medium text-contrast">Node</div>
          <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted">
            {current && (
              <span data-slot="run-on-session-node">Running on <span className="font-medium text-contrast">{running}</span>. </span>
            )}
            {choosing && (current
              ? "New sessions run on the node you pick; the current one stays where it is."
              : "New sessions run on the node you pick.")}
          </p>
        </div>
      </div>
      {runOnCompactAvailable && (
        <RunOnChoice compact value={nodeId} onChange={onChange} capability={AI_SESSION} disabled={disabled}
          loadNodes={() => Promise.resolve(list)} className="-mt-0.5 -mr-1 shrink-0" />
      )}
    </div>
  )
}
