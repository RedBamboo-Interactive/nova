import type { AgentInfo } from "./types"

export function orderAgentsByName(agents: readonly AgentInfo[]): AgentInfo[] {
  return [...agents].sort((left, right) =>
    left.name.localeCompare(right.name, undefined, { sensitivity: "base", numeric: true })
    || left.slug.localeCompare(right.slug)
    || left.id.localeCompare(right.id)
  )
}

export function getInitialAgentIndex(
  agents: readonly AgentInfo[],
  lastUsedAgentId: string | null,
): number {
  if (!lastUsedAgentId) return 0
  const index = agents.findIndex(agent => agent.id === lastUsedAgentId)
  return index >= 0 ? index : 0
}
