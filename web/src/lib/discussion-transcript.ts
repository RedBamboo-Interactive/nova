import type { MessageBlock, MessagePart } from "@redbamboo/chat"

/** Remove Nova's internal Meet Nova bootstrap from raw RedCompute history. */
export function filterInternalBootstrapBlock(
  blocks: MessageBlock[],
  bootstrapMessageUid?: string | null,
): MessageBlock[] {
  if (!bootstrapMessageUid) return blocks
  return blocks.filter((block) =>
    block.id !== bootstrapMessageUid
    && block.metadata?.messageUid !== bootstrapMessageUid)
}

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
 * Fingerprint durable transcript content while ignoring the streaming-only
 * presentation flag. A terminal status may finalize `isPartial` while a
 * canonical reload is in flight; that is not newer conversation content and
 * must not replace a richer fetched block.
 */
function messageContentFingerprint(message: MessageBlock): string {
  return JSON.stringify({
    ...message,
    parts: message.parts.map((part) => {
      if (!("isPartial" in part)) return part
      const contentPart = { ...part }
      delete contentPart.isPartial
      return contentPart
    }),
  })
}

function partWithoutStreamingState(part: MessagePart): Omit<MessagePart, "isPartial"> {
  const durable = { ...part }
  delete durable.isPartial
  return durable
}

function partContains(container: MessagePart, candidate: MessagePart): boolean {
  const durableContainer = partWithoutStreamingState(container)
  const durableCandidate = partWithoutStreamingState(candidate)

  if (container.type !== "text" && container.type !== "thinking") {
    return JSON.stringify(durableContainer) === JSON.stringify(durableCandidate)
  }

  const { content: containerContent, ...containerShape } = durableContainer
  const { content: candidateContent, ...candidateShape } = durableCandidate
  return JSON.stringify(containerShape) === JSON.stringify(candidateShape)
    && containerContent.startsWith(candidateContent)
}

/**
 * True when every durable part in `candidate` appears, in order, in
 * `container`. Text and thinking may be a longer completed form of a streamed
 * prefix. This is deliberately a dominance check rather than a general merge:
 * if two snapshots genuinely diverge, the live one remains safer.
 */
function messagePartsContain(container: MessagePart[], candidate: MessagePart[]): boolean {
  let containerIndex = 0
  for (const candidatePart of candidate) {
    while (containerIndex < container.length
      && !partContains(container[containerIndex], candidatePart)) {
      containerIndex++
    }
    if (containerIndex === container.length) return false
    containerIndex++
  }
  return true
}

/**
 * Merge a freshly fetched transcript with client-side changes that landed
 * while the request was in flight.
 *
 * New fetched blocks are authoritative, while matching blocks converge
 * monotonically because RedCompute's durable mirror is buffered behind its
 * live stream. A semantic superset wins because it fills a missed span without
 * discarding anything already visible. Genuinely divergent concurrent content
 * stays live, so a late response cannot roll an active tool call backwards.
 */
export function mergeRevalidatedMessages(
  authoritative: MessageBlock[],
  baseline: MessageBlock[],
  current: MessageBlock[],
): MessageBlock[] {
  const baselineFingerprints = new Map(
    baseline.map((message) => [message.id, messageContentFingerprint(message)]),
  )
  const changed = new Map<string, MessageBlock>()
  const currentById = new Map(current.map(message => [message.id, message]))

  for (const message of current) {
    const previous = baselineFingerprints.get(message.id)
    if (previous === undefined || previous !== messageContentFingerprint(message)) {
      changed.set(message.id, message)
    }
  }

  const merged = authoritative.map((message) => {
    const concurrent = changed.get(message.id)
    const existing = currentById.get(message.id)
    if (existing && messageFingerprint(existing) === messageFingerprint(message))
      return existing

    const local = concurrent ?? existing
    if (local) {
      if (messagePartsContain(message.parts, local.parts)) return message
      // Transcript records are append-only, while RedCompute ships ordinary
      // records through a buffered writer. A non-dominating snapshot may be a
      // different incomplete slice, so keep the local block until a later
      // snapshot proves it contains every visible part.
      return local
    }

    return message
  })
  const authoritativeIds = new Set(authoritative.map((message) => message.id))
  for (const message of changed.values()) {
    if (!authoritativeIds.has(message.id)) merged.push(message)
  }
  return merged
}
