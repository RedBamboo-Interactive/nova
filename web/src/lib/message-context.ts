import type { MessageBlock } from "@redbamboo/chat"

/**
 * The context the agent read ahead of one user message, as the history page returns it: the
 * message's stable uid and the metadata stored on its record (the Nova Context fields plus the
 * exact injected text under `novaContextBlock`).
 */
export interface DiscussionMessageContext {
  messageUid: string
  metadata: Record<string, unknown>
}

export type MessageContexts = Map<string, Record<string, unknown>>

/**
 * Adds a page's contexts to those already loaded for the discussion. A context and its message
 * can arrive on adjacent pages (the record is written just after the canonical one), so they are
 * kept per discussion beside the overlays and dropped with them. Anything malformed is ignored.
 */
export function accumulateMessageContexts(current: MessageContexts, incoming: unknown): MessageContexts {
  if (!Array.isArray(incoming) || incoming.length === 0) return current
  const next = new Map(current)
  for (const entry of incoming as Partial<DiscussionMessageContext>[]) {
    if (!entry || typeof entry.messageUid !== "string" || !entry.messageUid) continue
    const metadata = entry.metadata
    if (!metadata || typeof metadata !== "object" || Array.isArray(metadata)) continue
    next.set(entry.messageUid, metadata)
  }
  return next
}

/**
 * The metadata key of the exact injected text: the backend's NovaMessageContext.BlockKey and
 * @redbamboo/chat's MESSAGE_CONTEXT_BLOCK_KEY. Null on a user block says Nova has no stored
 * context for that message (it predates recording), so Message Info can say so.
 */
export const NOVA_CONTEXT_BLOCK_KEY = "novaContextBlock"

/**
 * Stamps each user block with the stored context of the message that has its uid, or marks it as
 * having none. Only the presentation metadata grows: the block's own keys (source, messageUid,
 * internal-input marks) win, and its id, parts, order and timestamp are untouched, so
 * reconciliation sees the same block.
 */
export function withMessageContexts(blocks: MessageBlock[], contexts: MessageContexts): MessageBlock[] {
  let changed = false
  const stamped = blocks.map(block => {
    if (block.role !== "user") return block
    const uid = typeof block.metadata?.messageUid === "string" && block.metadata.messageUid
      ? block.metadata.messageUid
      : block.id
    const context = contexts.get(uid)
    if (context) {
      changed = true
      return { ...block, metadata: { ...context, ...block.metadata } }
    }
    if (block.metadata && NOVA_CONTEXT_BLOCK_KEY in block.metadata) return block
    changed = true
    return { ...block, metadata: { ...block.metadata, [NOVA_CONTEXT_BLOCK_KEY]: null } }
  })
  return changed ? stamped : blocks
}
