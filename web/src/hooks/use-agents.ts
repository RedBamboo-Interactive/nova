import { useState, useEffect, useCallback } from "react"
import { api } from "../lib/api"
import type { AgentInfo } from "../lib/types"

export function useAgents() {
  const [agents, setAgents] = useState<AgentInfo[]>([])

  const refreshAgents = useCallback(() => {
    api.get<AgentInfo[]>("/api/apps/nova/agents").then(setAgents).catch(() => {})
  }, [])

  useEffect(() => {
    refreshAgents()

    // An outfit change replaces the effective avatar URL on the agent. Busting
    // the old image URL is not enough: every avatar consumer needs fresh agent
    // data so chat messages, the discussion list, and pickers all move together.
    window.addEventListener("nova:avatar-changed", refreshAgents)
    return () => window.removeEventListener("nova:avatar-changed", refreshAgents)
  }, [refreshAgents])

  const getAgent = useCallback(
    (id: string | null) => (id ? agents.find((a) => a.id === id) : undefined),
    [agents],
  )

  const defaultAgentId = agents.find((a) => a.slug === "nova")?.id ?? agents[0]?.id ?? null

  return { agents, getAgent, defaultAgentId }
}
