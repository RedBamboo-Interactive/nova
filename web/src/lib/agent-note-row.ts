import type { MessagePart } from "@redbamboo/chat"
import { AGENT_NOTE_KEY } from "./agent-note.ts"
import { NOTE_ROW_KEY } from "./message-order.ts"

/** What a reader's client knows about the discussion a note came from. */
export interface NoteOriginDiscussion {
  title: string | null
  agentId: string | null
  /** Closed discussions keep their name but can't be opened from a note. */
  archived: boolean
}

export interface NoteRowInput {
  /** Agent id or slug stored on the note; null when only the discussion is known. */
  agent: string | null
  discussionId: string | null
  topic: string | null
  currentDiscussionId: string | null
  /** Read at render: an outfit change or a retitle must show without reloading history. */
  resolveAgent: (ref: string) => { name: string; avatarUrl?: string | null } | undefined
  resolveDiscussion: (id: string) => NoteOriginDiscussion | undefined
}

export interface NoteRowDescription {
  name: string
  avatarUrl: string | null
  origin: string
  /** The discussion the origin opens; null when it can't be opened. */
  openDiscussionId: string | null
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-/i

function fallbackName(ref: string | null): string {
  if (!ref || GUID.test(ref)) return "An Agent"
  return `${ref.charAt(0).toUpperCase()}${ref.slice(1)}`
}

export function describeNote({ agent, discussionId, topic, currentDiscussionId, resolveAgent, resolveDiscussion }: NoteRowInput): NoteRowDescription {
  const discussion = discussionId ? resolveDiscussion(discussionId) : undefined
  // A bare-id source names only the discussion, and the discussion names its Agent.
  const ref = agent ?? discussion?.agentId ?? null
  const info = ref ? resolveAgent(ref) : undefined
  const name = info?.name ?? fallbackName(agent)
  const avatarUrl = info?.avatarUrl || null
  if (discussionId && discussionId === currentDiscussionId)
    return { name, avatarUrl, origin: "this discussion", openDiscussionId: null }
  if (discussion?.title)
    return { name, avatarUrl, origin: discussion.title, openDiscussionId: discussion.archived ? null : discussionId }
  return { name, avatarUrl, origin: topic || "another discussion", openDiscussionId: null }
}

/** The note as one plain line: the row truncates it, so Markdown markers would only show as noise. */
export function notePreview(text: string): string {
  return text
    .replace(/!\[[^\]]*\]\([^)]*\)/g, "")
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/^\s{0,3}(?:#{1,6}\s+|>\s?|[-*+]\s+|\d+[.)]\s+)/gm, "")
    .replace(/(\*\*|__|`+|~~)/g, "")
    .replace(/\s+/g, " ")
    .trim()
}

/** A note the conversation shows as a row. Anything else stays in the frieze. */
export function isNoteRowPart(part: MessagePart): boolean {
  if (part.toolName !== `event:${AGENT_NOTE_KEY}`) return false
  try {
    return (JSON.parse(part.toolInput ?? "{}") as { data?: Record<string, unknown> }).data?.[NOTE_ROW_KEY] === true
  } catch {
    return false
  }
}
