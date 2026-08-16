import type { DiscussionInfo } from "./types"
import { hasUnreadConversation } from "@redbamboo/chat/conversation-read-state"

export function isDiscussionUnread(discussion: DiscussionInfo): boolean {
  return discussion.type !== "heartbeat"
    && discussion.status === "idle"
    && hasUnreadConversation(discussion)
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
    }
  })
}

/** Apply a canonical conversational revision carried by a pushed message. */
export function applyConversationMessageArrival(
  discussions: DiscussionInfo[],
  discussionId: string,
  timestamp: string,
  conversationRevision: number,
  readConversationRevision?: number,
): DiscussionInfo[] {
  return discussions.map((discussion) => discussion.id !== discussionId
    ? discussion
    : {
        ...discussion,
        lastActivity: discussion.lastActivity > timestamp ? discussion.lastActivity : timestamp,
        messageCount: Math.max(1, discussion.messageCount),
        conversationRevision: Math.max(discussion.conversationRevision, conversationRevision),
        ...(readConversationRevision === undefined ? {} : {
          readConversationRevision: Math.max(
            discussion.readConversationRevision,
            readConversationRevision,
          ),
        }),
      })
}
