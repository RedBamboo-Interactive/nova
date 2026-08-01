import type { DiscussionInfo } from "./types"
import { resolveLiveSidebarSelection } from "./live-heartbeat.ts"

/** The exact discussion rows rendered by the sidebar, in visual order. */
export function getSidebarDiscussionOrder(
  discussions: DiscussionInfo[],
  agentFilter: string | null,
): DiscussionInfo[] {
  const scoped = agentFilter
    ? discussions.filter((discussion) => discussion.agentId === agentFilter)
    : discussions

  const live: DiscussionInfo[] = []
  const chat: DiscussionInfo[] = []
  for (const discussion of scoped) {
    if (discussion.type === "live") live.push(discussion)
    else if (discussion.type === "chat") chat.push(discussion)
  }
  return [...live, ...chat]
}

/** Resolve Alt+Up/Down against visible sidebar rows, including wraparound. */
export function getAdjacentSidebarDiscussion(
  discussions: DiscussionInfo[],
  activeDiscussionId: string | null,
  direction: -1 | 1,
  agentFilter: string | null,
): DiscussionInfo | null {
  const scoped = agentFilter
    ? discussions.filter((discussion) => discussion.agentId === agentFilter)
    : discussions
  const ordered = getSidebarDiscussionOrder(scoped, null)
  if (ordered.length === 0) return null

  const selectedId = resolveLiveSidebarSelection(scoped, activeDiscussionId)
  const index = ordered.findIndex((discussion) => discussion.id === selectedId)
  if (index < 0) return direction === 1 ? ordered[0] : ordered[ordered.length - 1]

  return ordered[(index + direction + ordered.length) % ordered.length]
}
