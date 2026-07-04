import { useState, useEffect, useCallback } from "react"
import { api } from "../lib/api"
import type { AgentInfo } from "../lib/types"

export function useAgents() {
  const [agents, setAgents] = useState<AgentInfo[]>([])

  useEffect(() => {
    api.get<AgentInfo[]>("/api/apps/nova/agents").then(setAgents).catch(() => {})
  }, [])

  const getAgent = useCallback(
    (id: string | null) => (id ? agents.find((a) => a.id === id) : undefined),
    [agents],
  )

  const defaultAgentId = agents.find((a) => a.slug === "nova")?.id ?? agents[0]?.id ?? null

  return { agents, getAgent, defaultAgentId }
}
