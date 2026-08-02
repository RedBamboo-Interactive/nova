import type { DiscussionMessage } from "./types.ts"

/**
 * The Nova discussion is the authorization boundary for its rendered history.
 * When the raw Compute transcript is available it wins because it carries tool
 * calls and thinking blocks; otherwise retain the discussion's transcript copy
 * instead of reducing the history to event markers.
 */
export function discussionMessagesForMerge(
  messages: DiscussionMessage[],
  hasRawSessionMessages: boolean,
): DiscussionMessage[] {
  return hasRawSessionMessages
    ? messages.filter((message) => message.source !== "session-transcript")
    : messages
}
