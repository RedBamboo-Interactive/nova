import type { MessageBlock, PersistedMessage } from "@redbamboo/chat"

/**
 * Nova chat's silent settle (the backend's NovaNoReply.Marker): an assistant final that is exactly
 * this ends the turn with no visible message. RedCompute keeps the record, so its transcript
 * sequence has no gap; only this projection hides it.
 */
export const NO_REPLY_MARKER = "<nova-no-reply/>"

/**
 * The whole marker, or the start of one still streaming in: deltas arrive as "<nova-", "no-re",
 * "ply/>", and none of them may flash. Text that leaves the marker's prefix shows as written.
 */
export function isSilentSettleText(content: string): boolean {
  const text = content.trim()
  return text.length > 0 && NO_REPLY_MARKER.startsWith(text)
}

/** Block metadata flag for a user record that Nova admitted as internal input (`internalMessage`). */
export const INTERNAL_INPUT_KEY = "internalInput"

/**
 * Carries each user record's admission metadata onto its rebuilt block. The history page strips
 * every <nova-context> from user text, so an internal note (a reaction note) arrives as "" and its
 * record metadata is the only thing left that says what it was.
 */
export function markInternalInputs(blocks: MessageBlock[], records: PersistedMessage[]): MessageBlock[] {
  const internal = new Set<string>()
  for (const record of records) {
    if (record.role !== "user" || !record.attachmentsJson) continue
    try {
      if (JSON.parse(record.attachmentsJson)?.metadata?.internalMessage === true)
        internal.add(record.messageUid || `db-${record.id}`)
    } catch { /* a garbled record is not evidence of anything */ }
  }
  if (internal.size === 0) return blocks
  return blocks.map(block => block.role === "user" && internal.has(block.id)
    ? { ...block, metadata: { ...block.metadata, [INTERNAL_INPUT_KEY]: true } }
    : block)
}

/**
 * Internal input with nothing for a person to see: a reaction note, whether it still holds its
 * envelope (raw session history) or the history page already stripped it to "" (then only its
 * record metadata identifies it). Anything a person wrote or attached, a "what I see" context card
 * (source="...") and a <nova-event> square still show, internal or not.
 */
export function isHiddenInternalInput(block: MessageBlock): boolean {
  if (block.role !== "user" || block.parts.length === 0) return false
  if (block.parts.some(part => part.type !== "text" || part.images?.length || part.attachments?.length)) return false
  const raw = block.parts.map(part => part.content).join("")
  if (/<nova-context\s+source="/.test(raw)) return false
  const visible = raw
    .replace(/<nova-context[\s\S]*?<\/nova-context>\s*/g, "")
    .replace(/<nova-prior-messages?[\s\S]*?<\/nova-prior-messages?>\s*/g, "")
    .trim()
  if (visible !== "") return false
  return block.metadata?.[INTERNAL_INPUT_KEY] === true || /<nova-context[\s>]/.test(raw)
}

/**
 * What the chat shows: no marker text (whole or partial), no assistant block left empty by it
 * (its tool squares and thinking still show), and no internal input with nothing to show (a
 * reaction note, live or from history). Returns the same array when nothing is hidden.
 */
export function hideSilentSettle(blocks: MessageBlock[]): MessageBlock[] {
  let changed = false
  const visible: MessageBlock[] = []
  for (const block of blocks) {
    if (isHiddenInternalInput(block)) {
      changed = true
      continue
    }
    if (block.role !== "assistant" || !block.parts.some(part => part.type === "text" && isSilentSettleText(part.content))) {
      visible.push(block)
      continue
    }
    changed = true
    const parts = block.parts.filter(part => !(part.type === "text" && isSilentSettleText(part.content)))
    if (parts.length > 0) visible.push({ ...block, parts })
  }
  return changed ? visible : blocks
}
