import type { AgentNotePresentation, ParsedEvent } from "@redbamboo/chat"
import { AGENT_NOTE_KEY, AGENT_NOTE_SENT_KEY } from "./agent-note.ts"

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

export interface SentNoteInput {
  targetDiscussionId: string | null
  targetTitle: string | null
  targetAgentId: string | null
  resolveAgent: NoteRowInput["resolveAgent"]
  resolveDiscussion: NoteRowInput["resolveDiscussion"]
}

export interface SentNoteDescription {
  /** Null when the target can't be named here, because it is confidential. */
  name: string | null
  avatarUrl: string | null
  target: string
  openDiscussionId: string | null
}

/** Who a sent note went to, read at render so a retitle or an outfit change shows without a reload. */
export function describeSentNote({ targetDiscussionId, targetTitle, targetAgentId, resolveAgent, resolveDiscussion }: SentNoteInput): SentNoteDescription {
  const discussion = targetDiscussionId ? resolveDiscussion(targetDiscussionId) : undefined
  const ref = targetAgentId ?? discussion?.agentId ?? null
  const info = ref ? resolveAgent(ref) : undefined
  const title = discussion?.title ?? targetTitle
  return {
    name: ref ? info?.name ?? fallbackName(ref) : null,
    avatarUrl: info?.avatarUrl || null,
    target: title ?? "another discussion",
    openDiscussionId: discussion && !discussion.archived ? targetDiscussionId : null,
  }
}

type AgentNoteResolvers = Pick<NoteRowInput, "currentDiscussionId" | "resolveAgent" | "resolveDiscussion"> & {
  onOpenDiscussion: (id: string) => void
}

function noteField(value: unknown): string | null {
  return typeof value === "string" && value ? value : null
}

export function describeAgentNote(event: ParsedEvent, options: AgentNoteResolvers): AgentNotePresentation | undefined {
  const { key, data } = event
  if (key !== AGENT_NOTE_KEY && key !== AGENT_NOTE_SENT_KEY) return undefined
  const sent = key === AGENT_NOTE_SENT_KEY
  const origin = sent
    ? describeSentNote({
      ...options,
      targetDiscussionId: noteField(data?.targetDiscussionId),
      targetTitle: noteField(data?.targetTitle),
      targetAgentId: noteField(data?.targetAgentId),
    })
    : describeNote({
      ...options,
      agent: noteField(data?.agent),
      discussionId: noteField(data?.discussionId),
      topic: noteField(data?.topic),
    })
  const id = origin.openDiscussionId
  return {
    lead: `Note ${sent ? "to" : "from"}${origin.name ? ` ${origin.name} in` : ""}`,
    discussion: "target" in origin ? origin.target : origin.origin,
    avatarUrl: origin.avatarUrl,
    discussionHref: id ? `/apps/nova/chat/${encodeURIComponent(id)}` : undefined,
    onOpenDiscussion: id ? () => options.onOpenDiscussion(id) : undefined,
  }
}
