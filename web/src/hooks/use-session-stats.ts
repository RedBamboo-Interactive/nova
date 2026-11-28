import { useState, useEffect, useRef, useMemo, useCallback } from "react"
import type { AccessLevel, SessionStats } from "@redbamboo/chat"
import { api } from "../lib/api"
import { readAccessLevel, readAccessLevels } from "../lib/session-access"

interface RedComputeSession {
  provider?: string
  providerEntity?: string
  qualityTier?: string
  model?: string
  status?: string
  startedAt?: string
  costUsd?: number
  costEstimated?: boolean
  messageCount?: number
  outputTokens?: number
  cachedInputTokens?: number
  promptCache?: SessionStats["promptCache"]
  contextTokens?: number
  contextWindow?: number
  effort?: string
  jobId?: string
  accessLevel?: unknown
  maxAccessLevel?: unknown
  supportedAccessLevels?: unknown
  /** The node the session runs on (Phase 1); absent from an older RedCompute. */
  nodeId?: string | null
  /** The folder the session works in (an Agent's workspace for a Nova discussion). */
  projectPath?: string | null
}

interface DiscussionRef {
  id: string
  title: string | null
  provider?: string | null
  qualityTier?: string | null
}

export interface SessionStatsState {
  stats: SessionStats | null
  /** Show the access settings the server just accepted. */
  applyAccessSettings: (sessionId: string, settings: { accessLevel?: AccessLevel; maxAccessLevel?: AccessLevel }) => void
  /** The node the session runs on, as the session says; null when unknown. */
  nodeId: string | null
  /** The folder the session works in, as the session says; null when unknown. */
  projectPath: string | null
}

export function useSessionStats(sessionId: string | null | undefined, isStreaming: boolean, discussion?: DiscussionRef | null): SessionStatsState {
  const [sessionData, setSessionData] = useState<RedComputeSession | null>(null)
  // Which session sessionData belongs to: a change accepted for one session never lands on another.
  const dataSessionRef = useRef<string | null>(null)
  const prevStreaming = useRef(isStreaming)

  useEffect(() => {
    const wasStreaming = prevStreaming.current
    prevStreaming.current = isStreaming
    if (!sessionId) {
      dataSessionRef.current = null
      setSessionData(null)
      return
    }

    if (dataSessionRef.current === sessionId && !(wasStreaming && !isStreaming)) return
    let cancelled = false

    const fetchStats = async () => {
      try {
        const data = await api.get<{ session: RedComputeSession }>(`/ai-session/sessions/${sessionId}?tail=1`)
        if (!cancelled && data.session) {
          dataSessionRef.current = sessionId
          setSessionData(data.session)
        }
      } catch {}
    }

    fetchStats()

    return () => { cancelled = true }
  }, [isStreaming, sessionId])

  const applyAccessSettings = useCallback((changedSessionId: string, settings: { accessLevel?: AccessLevel; maxAccessLevel?: AccessLevel }) => {
    if (dataSessionRef.current !== changedSessionId) return
    setSessionData(previous => previous ? { ...previous, ...settings } : previous)
  }, [])

  const stats = useMemo(() => {
    if (!sessionData || !sessionId || dataSessionRef.current !== sessionId) return null
    return {
      name: discussion?.title || undefined,
      sessionId,
      discussionId: discussion?.id,
      jobHash: sessionData.jobId,
      model: sessionData.model,
      status: sessionData.status,
      startedAt: sessionData.startedAt,
      costUsd: sessionData.costUsd,
      costEstimated: sessionData.costEstimated,
      messageCount: sessionData.messageCount,
      outputTokens: sessionData.outputTokens,
      cachedInputTokens: sessionData.cachedInputTokens,
      promptCache: sessionData.promptCache,
      contextTokens: sessionData.contextTokens,
      contextWindow: sessionData.contextWindow,
      effort: sessionData.effort,
      provider: discussion?.provider ?? sessionData.provider,
      providerEntity: discussion?.provider ?? sessionData.providerEntity,
      qualityTier: discussion?.qualityTier ?? sessionData.qualityTier,
      accessLevel: readAccessLevel(sessionData.accessLevel),
      maxAccessLevel: readAccessLevel(sessionData.maxAccessLevel),
      supportedAccessLevels: readAccessLevels(sessionData.supportedAccessLevels),
    }
  }, [sessionData, sessionId, discussion?.id, discussion?.title, discussion?.provider, discussion?.qualityTier])

  const nodeId = sessionData && sessionId && dataSessionRef.current === sessionId && typeof sessionData.nodeId === "string" ? sessionData.nodeId : null
  const projectPath = sessionData && sessionId && dataSessionRef.current === sessionId && typeof sessionData.projectPath === "string" ? sessionData.projectPath : null
  return useMemo(() => ({ stats, applyAccessSettings, nodeId, projectPath }), [stats, applyAccessSettings, nodeId, projectPath])
}
