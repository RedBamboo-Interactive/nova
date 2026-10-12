import type { AgentInfo } from "./types"

const coarsePrimaryPointerQuery = "(pointer: coarse)"

export function shouldAutoFocusNewDiscussionFilter(ownerWindow: Window): boolean {
  if (typeof ownerWindow.matchMedia !== "function") return true
  return !ownerWindow.matchMedia(coarsePrimaryPointerQuery).matches
}

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

export function reconcileHighlightedAgentId(
  agents: readonly AgentInfo[],
  currentAgentId: string | null,
  lastUsedAgentId: string | null,
): string | null {
  if (currentAgentId && agents.some(agent => agent.id === currentAgentId))
    return currentAgentId
  return agents[getInitialAgentIndex(agents, lastUsedAgentId)]?.id ?? null
}
