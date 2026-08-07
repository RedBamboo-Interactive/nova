// Subpath import, not the barrel: this module is exercised by node:test, which
// cannot load the barrel's React components.
import { isEventBlock } from "@redbamboo/chat/event-parts"
import type { MessageBlock, MessagePart } from "@redbamboo/chat"
import type { EventType } from "./types"

/**
 * Where frieze events sit in a discussion.
 *
 * Events (weather, music, discussion activity, device presence) are ambient
 * world facts that arrive asynchronously alongside the conversation. Exactly one
 * rule places them, and it holds for both the reloaded transcript and the live
 * stream:
 *
 *   **Timestamp order, with consecutive events merged into a single block** so a
 *   burst draws as one row of dots rather than one row per event.
 *
 * That is deliberately the whole rule. Earlier revisions tried to keep events
 * from landing inside an assistant "run" by holding them and hoisting them above
 * the run, which made placement depend on what came *later* in the discussion:
 * the same event rendered below the reply it followed, then above it as soon as
 * any further turn existed. Live and reloaded views disagreed for the same
 * reason.
 *
 * Ambient events are chronological boundaries even inside one assistant turn.
 * The stream opens a continuation block for later model activity, while the
 * shared activity projection joins adjacent segments into one frieze row. That
 * keeps the conversation turn intact without falsifying left-to-right order.
 */

export type EventTypeResolver = (source: string) => EventType | undefined

/** A frieze event as it arrives from the server, before becoming a message part. */
export interface FriezeEvent {
  /** Full source tag, e.g. `event:weather`. */
  source: string
  /** Event text, possibly still wrapped in a `<nova-event>` tag. */
  content: string
  /** Structured payload driving the rich event views; null for text-only events. */
  data: Record<string, unknown> | null
  timestamp: string
  senderAgentId?: string
}

export const byTimestamp = (a: MessageBlock, b: MessageBlock) =>
  new Date(a.timestamp ?? 0).getTime() - new Date(b.timestamp ?? 0).getTime()

/** Strip the `<nova-event>` carrier, keeping the text the event actually holds. */
function unwrapEventText(content: string): string {
  return content.replace(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/g, "$1").trim() || content
}

/**
 * True for a server message that should render as a frieze event rather than as
 * conversation. Two markers because older records predate the source metadata.
 */
export function isRawEventMessage(m: MessageBlock): boolean {
  const source = m.metadata?.source
  if (typeof source === "string" && source.startsWith("event:")) return true
  return /<nova-event\s/.test(m.parts[0]?.content ?? "")
}

function eventKey(source: string): string {
  return source.replace(/^event:/, "").split(":")[0] || "system"
}

export function buildEventPart(event: FriezeEvent, resolve?: EventTypeResolver): MessagePart {
  const eventType = resolve?.(event.source)
  const text = unwrapEventText(event.content)
  return {
    type: "tool_use",
    toolName: `event:${eventKey(event.source)}`,
    // The timestamp rides on the part, not the block: merged groups keep only
    // the first event's block timestamp, so the modal needs its own copy.
    toolInput: JSON.stringify({
      event: text,
      icon: eventType?.icon ?? null,
      color: eventType?.color ?? null,
      data: event.data,
      timestamp: event.timestamp,
    }),
    content: text,
  }
}

/** Read a persisted event message back into the shape `buildEventPart` expects. */
function toFriezeEvent(m: MessageBlock): FriezeEvent {
  const source = typeof m.metadata?.source === "string" ? m.metadata.source : "event:system"
  const data = m.metadata?.eventData
  return {
    source,
    content: m.parts[0]?.content ?? "",
    data: data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : null,
    timestamp: m.timestamp,
    senderAgentId: m.senderAgentId,
  }
}

function newEventBlock(event: FriezeEvent, part: MessagePart, index: number): MessageBlock {
  return {
    // Timestamp alone collides for events landing in the same millisecond, and a
    // duplicate React key drops blocks from the rendered list.
    id: `event-${event.timestamp}-${index}`,
    role: "assistant",
    parts: [part],
    timestamp: event.timestamp,
    senderAgentId: event.senderAgentId,
    metadata: { source: event.source },
  }
}

function eventPartTime(part: MessagePart): number {
  try {
    const payload = JSON.parse(part.toolInput ?? "{}") as { timestamp?: string }
    const time = payload.timestamp ? new Date(payload.timestamp).getTime() : Number.NaN
    return Number.isNaN(time) ? 0 : time
  } catch {
    return 0
  }
}

function chronologicalEventParts(parts: MessagePart[]): MessagePart[] {
  return parts
    .map((part, index) => ({ part, index }))
    .sort((a, b) => eventPartTime(a.part) - eventPartTime(b.part) || a.index - b.index)
    .map(({ part }) => part)
}

/**
 * Apply the ordering rule to a mixed list of conversation blocks and raw event
 * messages. Non-event blocks pass through untouched.
 */
export function orderMessages(blocks: MessageBlock[], resolve?: EventTypeResolver): MessageBlock[] {
  const out: MessageBlock[] = []
  for (const block of [...blocks].sort(byTimestamp)) {
    if (!isRawEventMessage(block)) {
      out.push(block)
      continue
    }
    const event = toFriezeEvent(block)
    const part = buildEventPart(event, resolve)
    const last = out[out.length - 1]
    if (last && isEventBlock(last)) {
      out[out.length - 1] = { ...last, parts: [...last.parts, part] }
      continue
    }
    out.push(newEventBlock(event, part, out.length))
  }
  return out
}

/**
 * The same rule applied incrementally, for an event arriving over the socket.
 * Relayed sockets can overtake one another, so insertion uses the authoritative
 * event timestamp rather than assuming browser receipt order.
 */
export function appendEvent(
  blocks: MessageBlock[],
  event: FriezeEvent,
  resolve?: EventTypeResolver,
): MessageBlock[] {
  const part = buildEventPart(event, resolve)
  let index = blocks.length
  const eventTime = new Date(event.timestamp).getTime()
  while (index > 0 && new Date(blocks[index - 1].timestamp ?? 0).getTime() > eventTime) index--

  const previous = blocks[index - 1]
  if (previous && isEventBlock(previous)) {
    return [
      ...blocks.slice(0, index - 1),
      { ...previous, parts: chronologicalEventParts([...previous.parts, part]) },
      ...blocks.slice(index),
    ]
  }
  const next = blocks[index]
  if (next && isEventBlock(next)) {
    return [
      ...blocks.slice(0, index),
      { ...next, timestamp: event.timestamp, parts: chronologicalEventParts([part, ...next.parts]) },
      ...blocks.slice(index + 1),
    ]
  }

  return [
    ...blocks.slice(0, index),
    newEventBlock(event, part, index),
    ...blocks.slice(index),
  ]
}
