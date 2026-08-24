import type { MessageBlock } from "@redbamboo/chat"
import { byTimestamp } from "./message-order.ts"

export interface NovaMessageArrival {
  content: string
  audioUrl?: string
  senderAgentId?: string
  messageUid?: string
  timestamp: string
  fallbackId: string
}

/**
 * Project one persisted Nova message into the live discussion view.
 *
 * New backends provide the canonical message UID. That lets duplicate socket
 * delivery collapse and lets a concurrent snapshot identify the same record.
 * During a mixed-version reload an older backend can omit the UID; keep that
 * legacy block visible, but do not mark it as a durable overlay that could sit
 * beside the canonical snapshot as a duplicate.
 */
export function mergeNovaMessageArrival(
  current: MessageBlock[],
  arrival: NovaMessageArrival,
): MessageBlock[] {
  if (arrival.messageUid && current.some(message => message.id === arrival.messageUid))
    return current

  const parts: MessageBlock["parts"] = [{ type: "text", content: arrival.content }]
  if (arrival.audioUrl) parts.push({ type: "audio", content: arrival.audioUrl })
  const block: MessageBlock = {
    id: arrival.messageUid ?? arrival.fallbackId,
    role: "assistant",
    parts,
    timestamp: arrival.timestamp,
    senderAgentId: arrival.senderAgentId,
    metadata: arrival.messageUid
      ? { source: "nova-message", messageUid: arrival.messageUid }
      : undefined,
  }
  return [...current, block].sort(byTimestamp)
}

function assistantTurnUid(block: MessageBlock): string | null {
  const uid = block.metadata?.messageUid
  return block.role === "assistant" && typeof uid === "string" && uid ? uid : null
}

/**
 * Rebuild Nova's record-shaped discussion response into the same assistant
 * turn segments used by the live stream. Consecutive records from one provider
 * turn are parts of one block; an intervening user/ambient record opens a new,
 * uniquely keyed segment while retaining the canonical turn uid in metadata.
 *
 * Response phases are append-only content. In particular, final_answer closes
 * a turn but never replaces an earlier commentary part.
 */
export function coalesceDiscussionTurnBlocks(blocks: MessageBlock[]): MessageBlock[] {
  const result: MessageBlock[] = []
  const segmentCounts = new Map<string, number>()

  for (const block of blocks) {
    const turnUid = assistantTurnUid(block)
    if (!turnUid) {
      result.push(block)
      continue
    }

    const previous = result[result.length - 1]
    if (previous && assistantTurnUid(previous) === turnUid) {
      result[result.length - 1] = {
        ...previous,
        parts: [...previous.parts, ...block.parts],
      }
      continue
    }

    const segment = segmentCounts.get(turnUid) ?? 0
    segmentCounts.set(turnUid, segment + 1)
    result.push({
      ...block,
      id: segment === 0 ? turnUid : `${turnUid}:segment:${segment}`,
      metadata: { ...block.metadata, messageUid: turnUid },
    })
  }

  return result
}

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
