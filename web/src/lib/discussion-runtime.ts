import type { DiscussionInfo } from "./types"

/**
 * Project RedCompute's provider-neutral session lifecycle onto the smaller
 * discussion lifecycle used by the list indicator.
 */
export function discussionStatusForSession(
  sessionStatus: string,
  discussionType: DiscussionInfo["type"],
): DiscussionInfo["status"] | null {
  if (sessionStatus === "Active") return "thinking"
  if (sessionStatus === "Idle" || sessionStatus === "Starting") return "idle"
  if (sessionStatus === "Stopped" || sessionStatus === "Error")
    return discussionType === "live" ? "idle" : "stopped"
  return null
}

export function applySessionStatus(
  discussions: DiscussionInfo[],
  discussionId: string,
  sessionStatus: string,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId || discussion.status === "archived" || discussion.status === "archiving")
      return discussion
    const status = discussionStatusForSession(sessionStatus, discussion.type)
    return status && status !== discussion.status ? { ...discussion, status } : discussion
  })
}
