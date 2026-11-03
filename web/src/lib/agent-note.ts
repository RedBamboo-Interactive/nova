/**
 * Notes one Agent instance posts into another instance's discussion.
 *
 * By convention the sender uses its own discussion as the event source,
 * `event:<agent>-<discussionId>` (e.g. `event:nova-855ddaca`), and opens the
 * text with `[internal] From <discussionId> (<topic>):`. Keying the frieze on
 * that per-instance source would give every sender its own unregistered event
 * type, so the projection folds them into one semantic `agent-note` key and
 * keeps the sender as structured data.
 */
export const AGENT_NOTE_KEY = "agent-note"

// Agent slugs may themselves contain hyphens and hex (`leni-b863d153`); the
// discussion is always the final eight hex digits.
const NOTE_SOURCE = /^(?:event:)?([a-z][a-z0-9-]*?)-([0-9a-f]{8})$/i
const NOTE_LEAD = /^\[internal\]\s*(?:From\s+([0-9a-f]{8})\b\s*(?:\(([^)\n]*)\))?\s*[:.]\s*)?/i
const BARE_DISCUSSION = /^[0-9a-f]{8}$/i
const DISCUSSION_SOURCE = /^discussion:([0-9a-f]{8})$/i

export interface AgentNote {
  /** Sender Agent slug from the source, e.g. `nova`. */
  agent: string
  /** The sender's own discussion, where replies belong. */
  discussionId: string
  /** What the sending instance is working on, when its lead names it. */
  topic: string | null
  /** Note text without the `[internal] From ...:` lead. */
  text: string
}

export function parseAgentNoteSource(source: string): { agent: string; discussionId: string } | null {
  const match = NOTE_SOURCE.exec(source)
  if (!match) return null
  return { agent: match[1]!.toLowerCase(), discussionId: match[2]!.toLowerCase() }
}

export function parseAgentNote(source: string, text: string): AgentNote | null {
  const sender = parseAgentNoteSource(source)
  if (!sender) return null
  const lead = NOTE_LEAD.exec(text)
  const body = lead ? text.slice(lead[0].length).trim() : text
  return {
    ...sender,
    topic: lead?.[2]?.trim() || null,
    text: body || text,
  }
}

/** A note the conversation shows as a row of its own, named after its sender. */
export interface NoteRowFields {
  /** Agent id or slug; null when only the sending discussion is known. */
  agent: string | null
  /** The sending discussion; null when the record doesn't say. */
  discussionId: string | null
  topic: string | null
  text: string
}

export interface NoteClassificationInput {
  /** Stored source, with or without the `event:` prefix. Server-held, never read from text. */
  source: string
  text: string
  senderAgentId?: string | null
  senderDiscussionId?: string | null
  /** The source is a registered machine type (authored or built in). */
  registered: boolean
}

function leadFields(text: string): { from: string | null; topic: string | null; body: string; internal: boolean } {
  const lead = NOTE_LEAD.exec(text)
  if (!lead) return { from: null, topic: null, body: text, internal: false }
  return {
    from: lead[1]?.toLowerCase() ?? null,
    topic: lead[2]?.trim() || null,
    body: text.slice(lead[0].length).trim() || text,
    internal: true,
  }
}

/**
 * Decides from the stored record whether an event is a note. Text alone never
 * makes one: the `[internal]` lead only confirms what the stored source or
 * sender already says, so a forged lead on any other source changes nothing.
 *
 * - A structured sender is a note unless its source is a registered machine type.
 * - `<agent>-<8hex>` names both sender and discussion; the lead confirms it.
 * - A bare `<8hex>` names only the discussion; the Agent is resolved at render.
 * - `discussion:<8hex>` is the registered activity type unless the lead's own
 *   `From <id>` names that same discussion.
 */
export function classifyNote({ source, text, senderAgentId, senderDiscussionId, registered }: NoteClassificationInput): NoteRowFields | null {
  const bare = source.replace(/^event:/, "")
  const lead = leadFields(text)
  if (senderAgentId) {
    if (registered) return null
    return {
      agent: senderAgentId,
      discussionId: senderDiscussionId ?? parseAgentNoteSource(bare)?.discussionId ?? lead.from,
      topic: lead.topic,
      text: lead.body,
    }
  }
  if (!lead.internal) return null
  const fromActivity = DISCUSSION_SOURCE.exec(bare)
  if (fromActivity) {
    const discussionId = fromActivity[1]!.toLowerCase()
    return lead.from === discussionId
      ? { agent: null, discussionId, topic: lead.topic, text: lead.body }
      : null
  }
  if (registered) return null
  if (BARE_DISCUSSION.test(bare))
    return { agent: null, discussionId: bare.toLowerCase(), topic: lead.topic, text: lead.body }
  const sender = parseAgentNoteSource(bare)
  return sender ? { ...sender, topic: lead.topic, text: lead.body } : null
}

/** Display text for a machine event: the `[internal]` marker is for the model. */
export function withoutInternalMarker(text: string): string {
  return text.replace(/^\[internal\]\s*/i, "") || text
}

export function agentNoteLabel(agent: string, discussionId: string): string {
  return `${agent.charAt(0).toUpperCase()}${agent.slice(1)} · ${discussionId}`
}
