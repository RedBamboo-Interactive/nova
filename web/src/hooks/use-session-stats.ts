import { useState, useEffect, useRef } from "react"
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
}

export function useSessionStats(sessionId: string | null | undefined, isStreaming: boolean): SessionStats | null {
  const [stats, setStats] = useState<SessionStats | null>(null)
  const prevStreaming = useRef(isStreaming)

  useEffect(() => {
    if (!sessionId) {
      setStats(null)
      return
    }

    let cancelled = false

    const fetchStats = async () => {
      try {
        const data = await api.get<{ session: RedComputeSession }>(`/ai-session/sessions/${sessionId}`)
        if (!cancelled && data.session) {
          setStats({
            model: data.session.model,
            status: data.session.status,
            startedAt: data.session.startedAt,
            costUsd: data.session.costUsd,
            messageCount: data.session.messageCount,
            outputTokens: data.session.outputTokens,
            cachedInputTokens: data.session.cachedInputTokens,
            contextTokens: data.session.contextTokens,
            contextWindow: data.session.contextWindow,
            effort: data.session.effort,
          })
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
          if (data.session) {
            setStats({
              model: data.session.model,
              status: data.session.status,
              startedAt: data.session.startedAt,
              costUsd: data.session.costUsd,
              messageCount: data.session.messageCount,
              outputTokens: data.session.outputTokens,
              cachedInputTokens: data.session.cachedInputTokens,
              contextTokens: data.session.contextTokens,
              contextWindow: data.session.contextWindow,
              effort: data.session.effort,
            })
          }
        })
        .catch(() => {})
    }
  }, [isStreaming, sessionId])

  return stats
}
