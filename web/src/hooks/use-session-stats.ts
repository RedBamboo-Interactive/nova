import { useState, useEffect, useRef, useMemo } from "react"
import type { SessionStats } from "@redbamboo/chat"
import { api } from "../lib/api"

interface RedComputeSession {
  model?: string
  status?: string
  startedAt?: string
  costUsd?: number
  messageCount?: number
  outputTokens?: number
  cachedInputTokens?: number
  contextTokens?: number
  contextWindow?: number
  effort?: string
  jobId?: string
}

interface DiscussionRef {
  id: string
  title: string | null
}

export function useSessionStats(sessionId: string | null | undefined, isStreaming: boolean, discussion?: DiscussionRef | null): SessionStats | null {
  const [sessionData, setSessionData] = useState<RedComputeSession | null>(null)
  const prevStreaming = useRef(isStreaming)

  useEffect(() => {
    if (!sessionId) {
      setSessionData(null)
      return
    }

    let cancelled = false

    const fetchStats = async () => {
      try {
        const data = await api.get<{ session: RedComputeSession }>(`/ai-session/sessions/${sessionId}`)
        if (!cancelled && data.session) setSessionData(data.session)
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
        .then(data => { if (data.session) setSessionData(data.session) })
        .catch(() => {})
    }
  }, [isStreaming, sessionId])

  return useMemo(() => {
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
      messageCount: sessionData.messageCount,
      outputTokens: sessionData.outputTokens,
      cachedInputTokens: sessionData.cachedInputTokens,
      contextTokens: sessionData.contextTokens,
      contextWindow: sessionData.contextWindow,
      effort: sessionData.effort,
    }
  }, [sessionData, sessionId, discussion?.id, discussion?.title])
}
