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
const NOTE_LEAD = /^\[internal\]\s*(?:From\s+[0-9a-f]{8}\b\s*(?:\(([^)\n]*)\))?\s*:\s*)?/i

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
    topic: lead?.[1]?.trim() || null,
    text: body || text,
  }
}

export function agentNoteLabel(agent: string, discussionId: string): string {
  return `${agent.charAt(0).toUpperCase()}${agent.slice(1)} · ${discussionId}`
}
