// Subpath import, not the barrel: this module is exercised by node:test, which
// cannot load the barrel's React components.
import { isEventBlock, isEventPart, eventInputMessageUid } from "@redbamboo/chat/event-parts"
import type { MessageBlock, MessagePart, PersistedMessage } from "@redbamboo/chat"
import type { EventType } from "./types"
import { readNoteSender, type NoteSender, AGENT_NOTE_KEY, agentNoteLabel, classifyNote, parseAgentNote, withoutInternalMarker } from "./agent-note.ts"
import { isUnregisteredEvent } from "./event-type-resolution.ts"

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
 *
 * The timestamp of an event a turn received is the moment it received it, like a
 * person's message: mergePagedDiscussionRecordsAndOverlays stamps it from the
 * record that carried it, and keeps the post time beside it as `postedAt`. Until
 * then it has only its post time, and the panel holds it with the queue if it waits.
 */

export type EventTypeResolver = (source: string) => EventType | undefined

/** Set in a note's event data when it reads as a row of its own, not a frieze dot. */
export const NOTE_ROW_KEY = "noteRow"

/** A frieze event as it arrives from the server, before becoming a message part. */
export interface FriezeEvent {
  /** Existing logical event/input UID, shared with the queue and history overlay. */
  messageUid?: string
  /** Full source tag, e.g. `event:weather`. */
  source: string
  /** Event text, possibly still wrapped in a `<nova-event>` tag. */
  content: string
  /** Structured payload driving the rich event views; null for text-only events. */
  data: Record<string, unknown> | null
  timestamp: string
  senderAgentId?: string
  /** Who wrote it, when the server stated that from a signed identity. */
  stampedSender?: NoteSender
  /** The transcript holds it where its turn received it: `timestamp` is that moment. */
  placed?: boolean
  /** When it was posted, for a placed event that has a discussion copy. */
  postedAt?: string
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
  if (m.metadata?.[PERSON_INPUT_KEY] === true) return false
  const source = m.metadata?.source
  if (typeof source === "string" && source.startsWith("event:")) return true
  return /<nova-event\s/.test(m.parts[0]?.content ?? "")
}

const WHOLE_EVENT_FRAME = /^\s*<nova-event\s+source="([^"]*)"([^>]*)>[\s\S]*<\/nova-event>\s*$/

/** Marks a person's own send whose text holds a frame: it is what they wrote, not an event. */
const PERSON_INPUT_KEY = "personInput"

function unescapeFrameAttribute(value: string): string {
  return value
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&")
}

/**
 * The transcript copy of an event is a user record holding one whole frame, and
 * until the stream record's overlay page is loaded it is the only copy: a
 * discussion's opening event sits before the first canonical record, on an
 * earlier overlay page. Without this it reads "System" until then.
 *
 * Who wrote a record is settled by its admission metadata, never by its text:
 *
 * - None: only the injector writes those, for an event with no sender.
 * - `novaEvent` and no `app`: the injector again, for an event with a sender.
 *   Only this kind can state a sender, in the frame's `from-*` attributes.
 * - `app`, not internal: a person's send (MessagePipeline, or a transport such as
 *   Discord). Frame text they typed stays their message.
 * - Anything else, including a coalesced batch, isn't one event: left alone.
 */
export function markEventFrameSources(blocks: MessageBlock[], records: PersistedMessage[]): MessageBlock[] {
  const marks = new Map<string, Record<string, unknown>>()
  for (const record of records) {
    if (record.role !== "user") continue
    const id = record.messageUid || `db-${record.id}`
    const content = record.content ?? ""
    const admission = readAdmission(record)
    if (admission === "person") {
      if (/<nova-event\s/.test(content)) marks.set(id, { [PERSON_INPUT_KEY]: true })
      continue
    }
    if (admission === "other") continue
    const frame = WHOLE_EVENT_FRAME.exec(content)
    if (!frame?.[1]) continue
    const sender = admission === "marked-event" ? frameSender(frame[2] ?? "") : undefined
    marks.set(id, {
      source: `event:${unescapeFrameAttribute(frame[1])}`,
      ...(sender ? { stampedSender: sender } : {}),
    })
  }
  if (marks.size === 0) return blocks
  return blocks.map(block => {
    const mark = block.role === "user" && !block.metadata?.source ? marks.get(block.id) : undefined
    return mark ? { ...block, metadata: { ...block.metadata, ...mark } } : block
  })
}

function readAdmission(record: PersistedMessage): "plain-event" | "marked-event" | "person" | "other" {
  if (!record.attachmentsJson) return "plain-event"
  try {
    const { inputMessageUids, metadata } = JSON.parse(record.attachmentsJson) ?? {}
    if (Array.isArray(inputMessageUids) && inputMessageUids.length > 1) return "other"
    if (!metadata || typeof metadata !== "object") return "other"
    if (metadata.novaEvent === true && metadata.app === undefined) return "marked-event"
    return typeof metadata.app === "string" && metadata.internalMessage !== true ? "person" : "other"
  } catch {
    return "other"
  }
}

function frameAttribute(attributes: string, name: string): string | null {
  const value = new RegExp(`\\s${name}="([^"]*)"`).exec(attributes)?.[1]
  return value ? unescapeFrameAttribute(value) : null
}

function frameSender(attributes: string): NoteSender | undefined {
  return readNoteSender({
    agentId: frameAttribute(attributes, "from-agent-id"),
    discussionId: frameAttribute(attributes, "from-discussion"),
    title: frameAttribute(attributes, "from-title"),
  })
}

function eventKey(source: string): string {
  return source.replace(/^event:/, "").split(":")[0] || "system"
}

export function buildEventPart(event: FriezeEvent, resolve?: EventTypeResolver): MessagePart {
  const eventType = resolve?.(event.source)
  const unwrapped = unwrapEventText(event.content)
  // A note type is the fold of `<agent>-<8hex>`, not somebody's registration.
  const registered = !!eventType && !isUnregisteredEvent(eventType) && eventType.key !== AGENT_NOTE_KEY
  const row = classifyNote({
    source: event.source,
    text: unwrapped,
    sender: event.stampedSender,
    registered,
  })
  // A source-shaped note without the lead keeps its named frieze dot (7977d9f1).
  const dot = row || registered ? null : parseAgentNote(eventKey(event.source), unwrapped)
  const isNote = !!row || !!dot
  const presentation = isNote ? resolve?.(`event:${AGENT_NOTE_KEY}`) ?? eventType : eventType
  const text = row?.text ?? dot?.text ?? withoutInternalMarker(unwrapped)
  const data = row
    ? { ...event.data, agent: row.agent, discussionId: row.discussionId, topic: row.topic, [NOTE_ROW_KEY]: true }
    : dot
      ? { ...event.data, agent: dot.agent, discussionId: dot.discussionId, topic: dot.topic }
      : event.data
  const noteLabel = row
    // A stated sender's `agent` is an id, not a name to print.
    ? row.agent && row.discussionId && !event.stampedSender ? agentNoteLabel(row.agent, row.discussionId) : presentation?.name ?? null
    : dot ? agentNoteLabel(dot.agent, dot.discussionId) : null
  return {
    type: "tool_use",
    ...(event.messageUid ? { messageUid: event.messageUid } : {}),
    ...(event.placed ? { inputPlaced: true } : {}),
    toolName: `event:${isNote ? AGENT_NOTE_KEY : eventKey(event.source)}`,
    // The timestamp rides on the part, not the block: merged groups keep only
    // the first event's block timestamp, so the modal needs its own copy.
    toolInput: JSON.stringify({
      event: text,
      label: isNote ? noteLabel : eventType?.name ?? null,
      // A source nobody registered shows its name beside its dot.
      inlineLabel: !isNote && !!eventType && isUnregisteredEvent(eventType),
      icon: presentation?.icon ?? null,
      color: presentation?.color ?? null,
      data,
      timestamp: event.timestamp,
      ...(event.postedAt ? { postedAt: event.postedAt } : {}),
    }),
    content: text,
  }
}

/**
 * Event types arrive after the first history load and projection isn't re-run
 * when they do, so the presentation parts were baked with can be stale: a source
 * that turned out to be registered would keep the unregistered name beside its
 * dot. This re-reads it at view time. Identity and content are untouched, and an
 * unchanged part stays the same object.
 */
export function withCurrentEventPresentation(blocks: MessageBlock[], resolve?: EventTypeResolver, cache = new WeakMap<MessagePart, MessagePart>()): MessageBlock[] {
  if (!resolve) return blocks
  let changed = false
  const next = blocks.map(block => {
    if (block.role === "user" || !block.parts.some(isPresentedEventPart)) return block
    let blockChanged = false
    const parts = block.parts.map(part => {
      if (!isPresentedEventPart(part)) return part
      let current = cache.get(part)
      if (!current) {
        current = refreshedEventPart(part, resolve)
        cache.set(part, current)
      }
      if (current !== part) blockChanged = true
      return current
    })
    if (!blockChanged) return block
    changed = true
    return { ...block, parts }
  })
  return changed ? next : blocks
}

function isPresentedEventPart(part: MessagePart): boolean {
  return isEventPart(part) && part.toolName !== `event:${AGENT_NOTE_KEY}`
}

function refreshedEventPart(part: MessagePart, resolve: EventTypeResolver): MessagePart {
  const type = resolve(part.toolName!)
  if (!type) return part
  try {
    const payload = JSON.parse(part.toolInput ?? "{}") as Record<string, unknown>
    const inlineLabel = isUnregisteredEvent(type)
    if (payload.label === type.name && payload.icon === type.icon && payload.color === type.color && !!payload.inlineLabel === inlineLabel)
      return part
    return { ...part, toolInput: JSON.stringify({ ...payload, label: type.name, icon: type.icon, color: type.color, inlineLabel }) }
  } catch {
    return part
  }
}

/** Read a persisted event message back into the shape `buildEventPart` expects. */
function toFriezeEvent(m: MessageBlock): FriezeEvent {
  const source = typeof m.metadata?.source === "string" ? m.metadata.source : "event:system"
  const data = m.metadata?.eventData
  return {
    source,
    messageUid: typeof m.metadata?.messageUid === "string" && m.metadata.messageUid ? m.metadata.messageUid : m.id,
    content: m.parts[0]?.content ?? "",
    data: data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : null,
    timestamp: m.timestamp,
    senderAgentId: m.senderAgentId,
    stampedSender: readNoteSender(m.metadata?.stampedSender),
    placed: m.metadata?.deliveryPlaced === true,
    postedAt: typeof m.metadata?.postedAt === "string" ? m.metadata.postedAt : undefined,
  }
}

function newEventBlock(event: FriezeEvent, part: MessagePart, index: number): MessageBlock {
  return {
    // Timestamp alone collides for events landing in the same millisecond, and a
    // duplicate React key drops blocks from the rendered list.
    id: event.messageUid ? `event-${event.messageUid}` : `event-${event.timestamp}-${index}`,
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
  const seen = new Set<string>()
  for (const block of [...blocks].sort(byTimestamp)) {
    if (!isRawEventMessage(block) && !isEventBlock(block)) {
      out.push(block)
      continue
    }
    // Already projected groups must retain every part and its identity/payload.
    const event = toFriezeEvent(block)
    const parts = isEventBlock(block) ? block.parts : [buildEventPart(event, resolve)]
    const unique = parts.filter(part => {
      const uid = eventInputMessageUid(part)
      if (!uid) return true
      if (seen.has(uid)) return false
      seen.add(uid)
      return true
    })
    if (!unique.length) continue
    const last = out[out.length - 1]
    if (last && isEventBlock(last)) {
      out[out.length - 1] = { ...last, parts: [...last.parts, ...unique] }
      continue
    }
    out.push(isEventBlock(block) ? { ...block, parts: unique } : newEventBlock(event, unique[0]!, out.length))
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
  if (event.messageUid && blocks.some(block => block.parts.some(part => eventInputMessageUid(part) === event.messageUid)))
    return blocks
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
