import type { AgentInfo } from "./types"

/**
 * `/apps/nova/chat/new?draft=<text>[&agent=<id or slug>]` opens a fresh discussion with `draft`
 * waiting in the composer, unsent. The Marketplace "Ask Nova" buttons link here, so the text
 * arrives from a URL anyone can craft: it is reduced to plain text the person can read in full
 * before choosing to send it.
 */

/** The shared composer's localStorage prefix for Nova drafts (`<key>:<discussionId>`). */
export const NOVA_DRAFT_STORAGE_KEY = "nova-drafts"

/** Code points kept from a linked draft. A `try` prompt is a sentence or two. */
export const MAX_LINKED_DRAFT_LENGTH = 2000

const MAX_AGENT_PARAM_LENGTH = 100

export interface NewDiscussionRequest {
  draft: string
  agent: string | null
}

export function parseNewDiscussionRequest(search: string): NewDiscussionRequest {
  const params = new URLSearchParams(search)
  const agent = params.get("agent")?.trim().slice(0, MAX_AGENT_PARAM_LENGTH) || null
  return { draft: sanitizeLinkedDraft(params.get("draft")), agent }
}

// C0 controls other than tab and newline, DEL and C1 controls.
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/g
// Characters that hide or reorder what the person sees: zero-width space, word joiner,
// byte order mark, and bidi embeddings, overrides and isolates. ZWJ/ZWNJ and LRM/RLM stay:
// emoji sequences and right-to-left text need them.
const INVISIBLE = /[\u200B\u2060\uFEFF\u202A-\u202E\u2066-\u2069]/g
// Nova's own envelope tags (<nova-context>, <nova-event>, ...): Nova strips them from history, so
// text inside them would reach the Agent while the transcript showed nothing.
const NOVA_TAG = /<\s*\/?\s*nova-[^>]*>/gi

/** Plain text for the composer: no controls, no hidden characters, no Nova tags, capped. */
export function sanitizeLinkedDraft(raw: string | null | undefined): string {
  if (!raw) return ""
  const text = raw
    .replace(/\r\n?/g, "\n")
    .replace(CONTROL, "")
    .replace(INVISIBLE, "")
    .replace(NOVA_TAG, "")
    .replace(/\n{3,}/g, "\n\n")
    .trim()
  const codePoints = Array.from(text)
  return codePoints.length > MAX_LINKED_DRAFT_LENGTH
    ? codePoints.slice(0, MAX_LINKED_DRAFT_LENGTH).join("").trimEnd()
    : text
}

/** The Agent a link names by id or slug; null (the default Agent) when it names none we know. */
export function resolveRequestedAgent(agents: readonly AgentInfo[], requested: string | null): AgentInfo | null {
  if (!requested) return null
  const wanted = requested.toLowerCase()
  return agents.find(agent => agent.id === requested)
    ?? agents.find(agent => agent.slug?.toLowerCase() === wanted)
    ?? null
}

/**
 * Navigation state asking the chat view to open on the conversation rather than the discussion
 * list on a phone. The new discussion is already active when the view mounts, so the view's own
 * "selection changed" switch never fires.
 */
export const OPEN_CHAT_STATE = { novaOpenChat: true } as const

export function opensChat(state: unknown): boolean {
  return typeof state === "object" && state !== null && (state as { novaOpenChat?: unknown }).novaOpenChat === true
}

/** Puts the draft where the composer restores it when the discussion opens. */
export function storeLinkedDraft(storage: Pick<Storage, "setItem">, discussionId: string, draft: string): boolean {
  if (!draft) return false
  try {
    storage.setItem(`${NOVA_DRAFT_STORAGE_KEY}:${discussionId}`, JSON.stringify({ text: draft, attachments: [] }))
    return true
  } catch {
    return false
  }
}
