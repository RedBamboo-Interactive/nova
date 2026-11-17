import { useEffect, useState } from "react"
import { RunOnChoice, fetchRunOnNodes, parseRunOnNodes, runOnView } from "@redbamboo/ui"
import { runOnSessionNode, type NodeListBody } from "../lib/run-on"

/**
 * @redbamboo/ui is the kernel's shared copy: a kernel built before RunOnChoice has none of these exports. Nova then
 * shows no "Run on" anywhere and discussions run automatically, sending exactly today's requests.
 */
export const runOnAvailable = !!RunOnChoice && !!fetchRunOnNodes && !!parseRunOnNodes && !!runOnView

/** The work a discussion's sessions do: nodes without AI sessions are listed disabled. */
export const AI_SESSION = { slug: "ai-session", name: "AI sessions" }

/** RunOnChoice as a compact dialog row above Provider and Tier: small text, a low trigger, never wrapping. */
export const RUN_ON_ROW = "min-h-7 min-w-0 px-2 text-xs whitespace-nowrap [&_[data-slot=run-on-choice-trigger]]:h-7 [&_[data-slot=run-on-choice-trigger]]:min-w-0 [&_[data-slot=run-on-choice-trigger]]:max-w-full [&_[data-slot=run-on-choice-trigger]]:text-xs"

/**
 * The discussion settings row (Phase 2 design 3.2): always "Running on <the session's node>" once a session
 * exists; with two or more nodes that can run sessions, a "New sessions run on" picker, Automatic first, with
 * the note that the current session stays where it is. One node shows no picker.
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
  if (!running && !picker) return null

  return (
    <div data-setting-path="discussion.runOn" className="flex items-start gap-2.5 rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2.5">
      <i className="ph-bold ph-desktop-tower mt-0.5 text-sm text-text-muted" aria-hidden="true" />
      <div className="min-w-0 flex-1 space-y-2">
        <div>
          <div className="text-xs font-medium text-contrast">Node</div>
          {running && hasSession && (
            <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted" data-slot="run-on-session-node">
              Running on <span className="font-medium text-contrast">{running}</span>
            </p>
          )}
        </div>
        {picker && (
          <div className="space-y-1.5">
            <div className="text-[11px] text-text-muted">New sessions run on</div>
            <RunOnChoice value={nodeId} onChange={onChange} capability={AI_SESSION} disabled={disabled}
              loadNodes={() => Promise.resolve(list)} />
            {running && hasSession && (
              <p className="text-[11px] leading-relaxed text-text-muted">
                The current session stays on {running}; the change applies from the next session.
              </p>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
