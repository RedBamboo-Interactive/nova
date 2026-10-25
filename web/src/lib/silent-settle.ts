import type { MessageBlock } from "@redbamboo/chat"

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

// A reaction note is a pure context envelope with nothing a person wrote. A "what I see"
// context (source="...") is a card the person attached, so it is not one.
function isEnvelopeOnly(block: MessageBlock): boolean {
  if (block.role !== "user" || block.parts.length === 0) return false
  if (block.parts.some(part => part.type !== "text" || part.images?.length || part.attachments?.length)) return false
  const raw = block.parts.map(part => part.content).join("")
  if (!/<nova-context[\s>]/.test(raw) || /<nova-context\s+source="/.test(raw)) return false
  return raw
    .replace(/<nova-context[\s\S]*?<\/nova-context>\s*/g, "")
    .replace(/<nova-prior-messages?[\s\S]*?<\/nova-prior-messages?>\s*/g, "")
    .trim() === ""
}

/**
 * What the chat shows: no marker text (whole or partial), no assistant block left empty by it
 * (its tool squares and thinking still show), and no envelope-only input (a reaction note,
 * live or from history). Returns the same array when nothing is hidden.
 */
export function hideSilentSettle(blocks: MessageBlock[]): MessageBlock[] {
  let changed = false
  const visible: MessageBlock[] = []
  for (const block of blocks) {
    if (isEnvelopeOnly(block)) {
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
