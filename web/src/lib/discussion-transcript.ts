import type { DiscussionMessage } from "./types.ts"
import type { MessageBlock } from "@redbamboo/chat"

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

function messageFingerprint(message: MessageBlock): string {
  return JSON.stringify(message)
}

/**
 * Merge a freshly fetched transcript with client-side changes that landed
 * while the request was in flight.
 *
 * The fetched transcript is authoritative for everything that was already on
 * screen when the request began. Blocks added or changed afterwards are kept:
 * those are optimistic sends and WebSocket stream updates that the snapshot
 * may have raced. Matching changed ids replace the fetched version so a late
 * response cannot roll an actively streaming tool call backwards.
 */
export function mergeRevalidatedMessages(
  authoritative: MessageBlock[],
  baseline: MessageBlock[],
  current: MessageBlock[],
): MessageBlock[] {
  const baselineFingerprints = new Map(
    baseline.map((message) => [message.id, messageFingerprint(message)]),
  )
  const changed = new Map<string, MessageBlock>()

  for (const message of current) {
    const previous = baselineFingerprints.get(message.id)
    if (previous === undefined || previous !== messageFingerprint(message)) {
      changed.set(message.id, message)
    }
  }

  const merged = authoritative.map((message) => changed.get(message.id) ?? message)
  const authoritativeIds = new Set(authoritative.map((message) => message.id))
  for (const message of changed.values()) {
    if (!authoritativeIds.has(message.id)) merged.push(message)
  }
  return merged
}
