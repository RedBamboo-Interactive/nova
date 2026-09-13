import type { MessageBlock, PersistedMessage } from "@redbamboo/chat"
import type { DiscussionHistoryOverlay } from "./types.ts"
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
  const representedInputs = new Set(sessionBlocks.filter(block => block.role === "user")
    .flatMap(block => block.inputMessageUids ?? []))
  const seen = new Set<string>()

  return [...discussionOnly, ...sessionBlocks]
    .filter((message) => {
      if (message.role === "user" && representedInputs.has(message.id) && !sessionBlocks.includes(message)) return false
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

/** Stable identity for a V2 overlay. Content and timestamp are never identity. */
export function historyOverlayIdentity(overlay: DiscussionHistoryOverlay): string {
  return overlay.messageUid ? `uid:${overlay.messageUid}` : `id:${overlay.id}`
}

/**
 * Add a server-authored overlay page to the already loaded overlay window.
 * Pages are immutable, but replacing the same stable identity makes retries
 * and a pushed-event/HTTP overlap idempotent.
 */
export function accumulateHistoryOverlays(
  current: Map<string, DiscussionHistoryOverlay>,
  incoming: DiscussionHistoryOverlay[],
): Map<string, DiscussionHistoryOverlay> {
  const next = new Map(current)
  for (const overlay of incoming)
    next.set(historyOverlayIdentity(overlay), overlay)
  return next
}

function stableBlockIdentity(block: MessageBlock): string {
  const messageUid = block.metadata?.messageUid
  return typeof messageUid === "string" && messageUid
    ? `uid:${messageUid}`
    : `id:${block.id}`
}

function stableRecordIdentity(record: PersistedMessage): string {
  return record.messageUid
    ? `uid:${record.messageUid}`
    : `id:${record.id}`
}

function timelineTimestamp(timestamp: string): number {
  const parsed = Date.parse(timestamp)
  return Number.isNaN(parsed) ? 0 : parsed
}

/** Match the canonical record order used by @redbamboo/chat rebuildBlocks. */
function orderPagedTranscriptRecords(records: PersistedMessage[]): PersistedMessage[] {
  const indexed = records.map((record, index) => ({ record, index }))
  const byLegacyTime = (a: typeof indexed[number], b: typeof indexed[number]) =>
    timelineTimestamp(a.record.timestamp) - timelineTimestamp(b.record.timestamp)
    || a.index - b.index
  const legacy = indexed
    .filter(({ record }) => record.sequence == null)
    .sort(byLegacyTime)
  const sequenced = indexed
    .filter(({ record }) => record.sequence != null)
    .sort((a, b) => {
      if (a.record.epoch !== b.record.epoch) return byLegacyTime(a, b)
      return a.record.sequence! - b.record.sequence! || a.index - b.index
    })
  return [...legacy, ...sequenced].map(({ record }) => record)
}

/**
 * Merge a V2 overlay window with raw transcript records before rebuilding
 * assistant turns. An ambient event is a chronological boundary: later parts
 * of a still-growing turn must remain after that event when history catches up.
 */
export function mergePagedDiscussionRecordsAndOverlays(
  overlayBlocks: MessageBlock[],
  records: PersistedMessage[],
  rebuildRecords: (records: PersistedMessage[]) => MessageBlock[],
): MessageBlock[] {
  const eventOverlayKeys = new Set(overlayBlocks
    .filter(block => typeof block.metadata?.source === "string"
      && block.metadata.source.startsWith("event:"))
    .map(stableBlockIdentity))
  const canonicalUserBlocks = rebuildRecords(records.filter(record => record.role === "user"))
  const canonicalKeys = new Set([
    ...records.filter(record => record.eventType !== "status")
      .map(stableRecordIdentity),
    ...canonicalUserBlocks.flatMap(block => (block.inputMessageUids ?? []).flatMap(uid => [`uid:${uid}`, `id:${uid}`])),
  ])

  const retainedRecords = records.filter(record =>
    !eventOverlayKeys.has(stableRecordIdentity(record)))
  const retainedOverlays = overlayBlocks.filter(block => {
    const source = block.metadata?.source
    return (typeof source === "string" && source.startsWith("event:"))
      || !canonicalKeys.has(stableBlockIdentity(block))
  })

  const orderedRecords = orderPagedTranscriptRecords(retainedRecords)
  const orderedOverlays = retainedOverlays
    .map((block, order) => ({ block, order }))
    .sort((a, b) =>
      timelineTimestamp(a.block.timestamp) - timelineTimestamp(b.block.timestamp)
      || a.order - b.order)
    .map(({ block }) => block)

  const merged: MessageBlock[] = []
  let recordSpan: PersistedMessage[] = []
  let overlayIndex = 0
  const flushRecords = () => {
    if (recordSpan.length === 0) return
    merged.push(...rebuildRecords(recordSpan))
    recordSpan = []
  }

  for (const record of orderedRecords) {
    while (overlayIndex < orderedOverlays.length
      && timelineTimestamp(orderedOverlays[overlayIndex]!.timestamp)
        <= timelineTimestamp(record.timestamp)) {
      flushRecords()
      merged.push(orderedOverlays[overlayIndex]!)
      overlayIndex++
    }
    recordSpan.push(record)
  }
  flushRecords()
  while (overlayIndex < orderedOverlays.length) {
    merged.push(orderedOverlays[overlayIndex]!)
    overlayIndex++
  }

  return coalesceDiscussionTurnBlocks(merged)
}

/**
 * Merge a V2 overlay window with canonical session blocks using only stable
 * identities. Ambient events keep Nova's richer event projection; canonical
 * transcript blocks win over user bridges and injected Nova-message copies.
 */
export function mergePagedDiscussionAndSessionBlocks(
  overlayBlocks: MessageBlock[],
  sessionBlocks: MessageBlock[],
): MessageBlock[] {
  const eventOverlayKeys = new Set(overlayBlocks
    .filter(block => typeof block.metadata?.source === "string"
      && block.metadata.source.startsWith("event:"))
    .map(stableBlockIdentity))
  const canonicalKeys = new Set(sessionBlocks.flatMap(block => [
    stableBlockIdentity(block),
    ...(block.role === "user" ? block.inputMessageUids ?? [] : []).map(uid => `uid:${uid}`),
    ...(block.role === "user" ? block.inputMessageUids ?? [] : []).map(uid => `id:${uid}`),
  ]))

  const retainedSession = sessionBlocks.filter(block => !eventOverlayKeys.has(stableBlockIdentity(block)))
  const retainedOverlays = overlayBlocks.filter(block => {
    const source = block.metadata?.source
    return (typeof source === "string" && source.startsWith("event:"))
      || !canonicalKeys.has(stableBlockIdentity(block))
  })

  return [...retainedOverlays, ...retainedSession].sort(byTimestamp)
}
