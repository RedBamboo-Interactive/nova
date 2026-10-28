import { useEffect, useMemo, useState } from "react"
import { useLocation, useNavigate } from "react-router-dom"
import { useUiEnvironment } from "@redbamboo/ui"
import { useDisc } from "../App"
import { api } from "../lib/api"
import {
  OPEN_CHAT_STATE,
  parseNewDiscussionRequest,
  resolveRequestedAgent,
  storeLinkedDraft,
} from "../lib/new-discussion-draft"
import type { AgentInfo } from "../lib/types"

/**
 * `/apps/nova/chat/new?draft=...&agent=...`: starts a fresh discussion with the requested Agent
 * (the default Agent when the link names none we know), leaves `draft` in its composer unsent,
 * and replaces this URL with the discussion's own so Back and reload never start another one.
 */
export function NewDiscussionRoute() {
  const location = useLocation()
  const navigate = useNavigate()
  const environment = useUiEnvironment()
  const { createDiscussion } = useDisc()
  const [attempt, setAttempt] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const request = useMemo(() => parseNewDiscussionRequest(location.search), [location.search])
  // One key per visit and retry: a re-run effect (StrictMode, a re-render) reuses the discussion
  // the first request created rather than creating a second.
  const idempotencyKey = useMemo(
    () => `nova-new:${location.key}:${attempt}:${globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)}`,
    [location.key, attempt],
  )

  useEffect(() => {
    let cancelled = false
    setError(null)
    void (async () => {
      try {
        const agents = request.agent
          ? await api.get<AgentInfo[]>("/api/apps/nova/agents").catch(() => [] as AgentInfo[])
          : []
        const agent = resolveRequestedAgent(agents, request.agent)
        const discussion = await createDiscussion(agent?.id, undefined, undefined, { idempotencyKey })
        if (cancelled) return
        storeLinkedDraft(environment.window.localStorage, discussion.id, request.draft)
        navigate(`/apps/nova/chat/${discussion.id}`, { replace: true, state: OPEN_CHAT_STATE })
      } catch (cause) {
        if (!cancelled) setError(cause instanceof Error ? cause.message : "The discussion could not be started.")
      }
    })()
    return () => { cancelled = true }
  }, [createDiscussion, environment.window, idempotencyKey, navigate, request])

  return (
    <div className="flex h-full items-center justify-center px-4">
      {error ? (
        <div
          role="alert"
          data-slot="new-discussion-error"
          className="w-full max-w-sm rounded-xl border border-destructive-a30 bg-card px-4 py-3 text-center shadow-lg"
        >
          <i aria-hidden="true" className="ph-bold ph-warning-circle text-xl text-destructive" />
          <p className="mt-1 text-sm font-semibold text-contrast">Couldn&apos;t start a new discussion</p>
          <p className="mt-1 break-words text-xs leading-relaxed text-text-muted">{error}</p>
          <div className="mt-3 flex justify-center gap-2">
            <button
              type="button"
              onClick={() => setAttempt(value => value + 1)}
              className="inline-flex h-8 items-center justify-center rounded-md bg-primary px-3 text-xs font-semibold text-primary-foreground transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
            >
              Retry
            </button>
            <button
              type="button"
              onClick={() => navigate("/apps/nova/chat", { replace: true })}
              className="inline-flex h-8 items-center justify-center rounded-md border border-border px-3 text-xs font-semibold text-text-default transition-colors hover:bg-bg-active focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
            >
              Back to chat
            </button>
          </div>
        </div>
      ) : (
        <div role="status" className="flex items-center gap-2 text-sm text-text-muted">
          <i aria-hidden="true" className="ph-bold ph-spinner animate-spin text-xl opacity-60" />
          Starting a new discussion…
        </div>
      )}
    </div>
  )
}
