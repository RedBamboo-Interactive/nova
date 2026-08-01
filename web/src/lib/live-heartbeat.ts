import type { DiscussionInfo } from "./types"

export interface LiveHeartbeatPair {
  live: DiscussionInfo
  heartbeat: DiscussionInfo
}

/** Find the current Live/Heartbeat views for the active discussion's agent. */
export function findLiveHeartbeatPair(
  discussions: DiscussionInfo[],
  activeDiscussion: DiscussionInfo | null,
): LiveHeartbeatPair | null {
  if (!activeDiscussion || (activeDiscussion.type !== "live" && activeDiscussion.type !== "heartbeat")) {
    return null
  }

  const isCurrentForAgent = (discussion: DiscussionInfo) =>
    discussion.agentId === activeDiscussion.agentId
    && discussion.status !== "archived"
    && discussion.status !== "archiving"

  const live = discussions.find((discussion) => discussion.type === "live" && isCurrentForAgent(discussion))
  const heartbeat = discussions.find((discussion) => discussion.type === "heartbeat" && isCurrentForAgent(discussion))
  return live && heartbeat ? { live, heartbeat } : null
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
