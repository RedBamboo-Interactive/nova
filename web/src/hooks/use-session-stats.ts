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
  contextTokens?: number
  contextWindow?: number
  effort?: string
  jobId?: string
  accessLevel?: unknown
  maxAccessLevel?: unknown
  supportedAccessLevels?: unknown
}

interface DiscussionRef {
  id: string
  title: string | null
  provider?: string | null
  qualityTier?: string | null
}

export interface SessionStatsState {
  stats: SessionStats | null
  /** Show a level the server just accepted, until the next read of the session says it too. */
  applyAccessLevel: (sessionId: string, level: AccessLevel) => void
}

export function useSessionStats(sessionId: string | null | undefined, isStreaming: boolean, discussion?: DiscussionRef | null): SessionStatsState {
  const [sessionData, setSessionData] = useState<RedComputeSession | null>(null)
  // Which session sessionData belongs to: a change accepted for one session never lands on another.
  const dataSessionRef = useRef<string | null>(null)
  const prevStreaming = useRef(isStreaming)

  useEffect(() => {
    if (!sessionId) {
      dataSessionRef.current = null
      setSessionData(null)
      return
    }

    let cancelled = false

    const fetchStats = async () => {
      try {
        const data = await api.get<{ session: RedComputeSession }>(`/ai-session/sessions/${sessionId}`)
        if (!cancelled && data.session) {
          dataSessionRef.current = sessionId
          setSessionData(data.session)
        }
      } catch {}
    }

    fetchStats()

    return () => { cancelled = true }
  }, [sessionId])

  useEffect(() => {
    const wasStreaming = prevStreaming.current
    prevStreaming.current = isStreaming

    if (wasStreaming && !isStreaming && sessionId) {
      api.get<{ session: RedComputeSession }>(`/ai-session/sessions/${sessionId}`)
        .then(data => {
          if (!data.session) return
          dataSessionRef.current = sessionId
          setSessionData(data.session)
        })
        .catch(() => {})
    }
  }, [isStreaming, sessionId])

  const applyAccessLevel = useCallback((changedSessionId: string, level: AccessLevel) => {
    if (dataSessionRef.current !== changedSessionId) return
    setSessionData(previous => previous ? { ...previous, accessLevel: level } : previous)
  }, [])

  const stats = useMemo(() => {
    if (!sessionData || !sessionId) return null
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

  return useMemo(() => ({ stats, applyAccessLevel }), [stats, applyAccessLevel])
}
