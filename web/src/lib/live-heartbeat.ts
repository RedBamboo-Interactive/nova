import type { DiscussionInfo } from "./types"

export interface LiveHeartbeatPair {
  live: DiscussionInfo
  /** Absent for an Agent without a Heartbeat: Live and Journal are still its views. */
  heartbeat: DiscussionInfo | null
}

/** Find the current Live/Heartbeat views for the active discussion's agent. */
export function findLiveHeartbeatPair(
  discussions: DiscussionInfo[],
  activeDiscussion: DiscussionInfo | null,
): LiveHeartbeatPair | null {
  if (!activeDiscussion || (activeDiscussion.type !== "live" && activeDiscussion.type !== "heartbeat")) {
    return null
  }

  const isOpen = (discussion: DiscussionInfo) =>
    discussion.status !== "archived" && discussion.status !== "archiving"
  // An archived Live or Heartbeat is a past day, not the current pair: it
  // keeps its own titled header instead of the switcher.
  if (!isOpen(activeDiscussion)) return null

  const isCurrentForAgent = (discussion: DiscussionInfo) =>
    discussion.agentId === activeDiscussion.agentId && isOpen(discussion)

  const live = discussions.find((discussion) => discussion.type === "live" && isCurrentForAgent(discussion))
  const heartbeat = discussions.find((discussion) => discussion.type === "heartbeat" && isCurrentForAgent(discussion))
  return live ? { live, heartbeat: heartbeat ?? null } : null
}

/** The current Live of one Agent, or with no Agent named the first current Live in the list. */
export function findCurrentLive(discussions: DiscussionInfo[], agentId: string | null): DiscussionInfo | null {
  return discussions.find((discussion) => discussion.type === "live"
    && discussion.status !== "archived" && discussion.status !== "archiving"
    && (agentId === null || discussion.agentId === agentId)) ?? null
}

/** Keep the agent's single Live sidebar surface selected while viewing Heartbeat. */
export function resolveLiveSidebarSelection(
  discussions: DiscussionInfo[],
  activeDiscussionId: string | null,
): string | null {
  const activeDiscussion = discussions.find((discussion) => discussion.id === activeDiscussionId) ?? null
  if (activeDiscussion?.type !== "heartbeat") return activeDiscussionId
  return findLiveHeartbeatPair(discussions, activeDiscussion)?.live.id ?? activeDiscussionId
}
