import type { DiscussionInfo } from "./types"

export function isDiscussionUnread(discussion: DiscussionInfo): boolean {
  return discussion.type !== "heartbeat"
    && discussion.status === "idle"
    && discussion.messageCount > 0
    && (!discussion.lastReadAt || discussion.lastActivity > discussion.lastReadAt)
}

/**
 * Project a persisted message arrival into the mounted discussion list.
 * The server remains authoritative after refresh; this keeps the pushed UI
 * coherent between that arrival and the next canonical fetch.
 */
export function applyDiscussionMessageArrival(
  discussions: DiscussionInfo[],
  discussionId: string,
  timestamp: string,
  readAt: string | null,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId) return discussion

    return {
      ...discussion,
      lastActivity: discussion.lastActivity > timestamp ? discussion.lastActivity : timestamp,
      // Multiple mounted Nova surfaces share this list store and can project
      // the same socket frame. Only establish that content exists here; the
      // next server refresh supplies the canonical exact count.
      messageCount: Math.max(1, discussion.messageCount),
      ...(readAt ? { lastReadAt: readAt } : {}),
    }
  })
}
