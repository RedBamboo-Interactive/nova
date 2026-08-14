import type { MessageBlock } from "@redbamboo/chat"

/**
 * Combine Nova's authorized discussion projection with RedCompute's richer raw
 * transcript. The discussion endpoint contributes ambient events and a newly
 * accepted user-message bridge while the session mirror catches up; once raw
 * session history is available, its tool/thinking parts must remain canonical.
 */
export function mergeDiscussionAndSessionBlocks(
  discussionBlocks: MessageBlock[],
  sessionBlocks: MessageBlock[],
  normalizeUserContent: (content: string) => string = (content) => content,
): MessageBlock[] {
  const discussionOnly = sessionBlocks.length > 0
    ? discussionBlocks.filter((message) => message.metadata?.source !== "session-transcript")
    : discussionBlocks
  const seen = new Set<string>()

  return [...discussionOnly, ...sessionBlocks]
    .filter((message) => {
      const idKey = message.id == null ? null : `id:${message.id}`
      const content = message.parts[0]?.content ?? ""
      const dedupContent = message.role === "user" ? normalizeUserContent(content) : content
      const timestamp = message.timestamp.replace(/\+00:00$/, "Z")
      const fallbackKey = `content:${timestamp}:${dedupContent.slice(0, 50)}`
      if ((idKey !== null && seen.has(idKey)) || seen.has(fallbackKey)) return false
      if (idKey !== null) seen.add(idKey)
      seen.add(fallbackKey)
      return true
    })
    .sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp))
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
  const currentById = new Map(current.map(message => [message.id, message]))

  for (const message of current) {
    const previous = baselineFingerprints.get(message.id)
    if (previous === undefined || previous !== messageFingerprint(message)) {
      changed.set(message.id, message)
    }
  }

  const merged = authoritative.map((message) => {
    const concurrent = changed.get(message.id)
    if (concurrent) return concurrent
    const existing = currentById.get(message.id)
    return existing && messageFingerprint(existing) === messageFingerprint(message)
      ? existing
      : message
  })
  const authoritativeIds = new Set(authoritative.map((message) => message.id))
  for (const message of changed.values()) {
    if (!authoritativeIds.has(message.id)) merged.push(message)
  }
  return merged
}
