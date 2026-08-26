import type { DiscussionInfo } from "./types"

/**
 * Project RedCompute's provider-neutral session lifecycle onto the smaller
 * discussion lifecycle used by the list indicator.
 */
export function discussionStatusForSession(
  sessionStatus: string,
  _discussionType: DiscussionInfo["type"],
  stopReason?: string,
): DiscussionInfo["status"] | null {
  if (sessionStatus === "Active") return "thinking"
  if (sessionStatus === "Idle" || sessionStatus === "Starting") return "idle"
  if (sessionStatus === "Stopped"
      && (stopReason === "maintenance_restart" || stopReason === "orphaned_on_restart"))
    return "idle"
  if (sessionStatus === "Stopped" || sessionStatus === "Error")
    return "stopped"
  return null
}

export function applySessionStatus(
  discussions: DiscussionInfo[],
  discussionId: string,
  sessionStatus: string,
  stopReason?: string,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId || discussion.status === "archived" || discussion.status === "archiving")
      return discussion
    const status = discussionStatusForSession(sessionStatus, discussion.type, stopReason)
    return status && status !== discussion.status ? { ...discussion, status } : discussion
  })
}

/**
 * Apply a non-active provider status and the activity metadata emitted when a
 * turn settles. Keeping this projection pure prevents the WebSocket handler
 * from accidentally preserving the very `thinking` state it needs to clear.
 */
export function applySettledSessionStatus(
  discussions: DiscussionInfo[],
  discussionId: string,
  sessionStatus: string,
  activityAt: string,
  stopReason?: string,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId || discussion.status === "archived" || discussion.status === "archiving")
      return discussion
    const status = discussionStatusForSession(sessionStatus, discussion.type, stopReason)
    if (!status || status === "thinking") return discussion
    return {
      ...discussion,
      status,
      lastActivity: activityAt,
    }
  })
}

/**
 * A newly submitted local turn can be durably delivered before RedCompute's
 * provider lifecycle reaches Active. Ignore a transient non-active status only
 * until this client observes Active. Once Active was observed, Idle is the
 * genuine settlement of that turn even when it completed inside the grace
 * window.
 */
export function preservesRecentStreamingLatch(
  sessionStatus: string,
  isStreaming: boolean,
  lastSendAt: number,
  now: number,
  graceMs: number,
  observedActiveAfterSend = false,
): boolean {
  return isStreaming
    && !observedActiveAfterSend
    && sessionStatus !== "Stopped"
    && sessionStatus !== "Error"
    && now - lastSendAt < graceMs
}
