// "Ask Nova about this" for the Leaf context menu: Nova contributes to the shell's
// entity, image and selection kinds. The item opens a new discussion through the
// existing `/apps/nova/chat/new?draft=` route, which leaves the reference in the
// composer unsent. No React or shell imports, so node tests load it directly.
import type { ContextMenuEntry, ContextMenuProvider } from "@redbamboo/ui"
import { MAX_LINKED_DRAFT_LENGTH } from "./new-discussion-draft.ts"

/** What an entity target or the shell's entity kind hands contributors. */
export interface EntityReference {
  id: string
  typeSlug: string
  name?: string
}

/**
 * One item per kind. An entity card's image and selected text inside a card can
 * share a menu with the card itself, so each says what it is about.
 */
export const ASK_NOVA_ITEMS = {
  entity: { id: "ask-nova-entity", label: "Ask Nova about this" },
  image: { id: "ask-nova-image", label: "Ask Nova about this image" },
  selection: { id: "ask-nova-selection", label: "Ask Nova about the selection" },
} as const

const ASK_ICON = "ph-bold ph-sparkle"

/** The route that starts a discussion with `draft` waiting in the composer. */
export function newDiscussionRoute(draft: string): string {
  return `/apps/nova/chat/new?draft=${encodeURIComponent(draft)}`
}

function escapeLinkText(text: string): string {
  return text.replace(/[\\[\]]/g, (char) => `\\${char}`)
}

/** A standalone `redleaf://` link, which chat renders as the entity's card. */
export function entityDraft(entity: EntityReference): string | null {
  if (!entity.id || !entity.typeSlug) return null
  const name = entity.name?.trim() || entity.typeSlug
  return `[${escapeLinkText(name)}](redleaf://${encodeURIComponent(entity.typeSlug)}/${encodeURIComponent(entity.id)})`
}

// encodeURIComponent leaves parentheses alone; they would end the Markdown link.
const MARKDOWN_URL_ESCAPES: Record<string, string> = { "(": "%28", ")": "%29" }

/**
 * The image as Markdown, by address: same-origin assets keep their path so the
 * reference survives the tunnel. Inline (`data:`) and page-local (`blob:`) images
 * have no address an Agent could read, so they get no item.
 */
export function imageDraft(src: string, origin: string): string | null {
  let url: URL
  try {
    url = new URL(src, origin)
  } catch {
    return null
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return null
  const address = url.origin === origin ? `${url.pathname}${url.search}` : url.href
  return `![image](${address.replace(/[()\s]/g, (char) => MARKDOWN_URL_ESCAPES[char] ?? encodeURIComponent(char))})`
}

/** The selection as a Markdown quote, cut to what a linked draft keeps. */
export function selectionDraft(text: string): string | null {
  const lines = text.replace(/\r\n?/g, "\n").trim().split("\n").map((line) => line.trimEnd())
  if (!lines.some(Boolean)) return null
  let quote = lines.map((line) => (line ? `> ${line}` : ">")).join("\n")
  const points = Array.from(quote)
  if (points.length > MAX_LINKED_DRAFT_LENGTH) quote = `${points.slice(0, MAX_LINKED_DRAFT_LENGTH - 1).join("").trimEnd()}…`
  return quote
}

export function askNovaEntries(kind: keyof typeof ASK_NOVA_ITEMS, draft: string | null): ContextMenuEntry[] {
  if (!draft) return []
  return [{ ...ASK_NOVA_ITEMS[kind], icon: ASK_ICON, run: (ctx) => ctx.navigate(newDiscussionRoute(draft)) }]
}

/** Nova's contributions; the shell registers them while the Leaf context menu is on. */
export const ASK_NOVA_PROVIDERS: ContextMenuProvider[] = [
  {
    id: "ask-nova-entity",
    kind: "entity",
    getItems: (entity: EntityReference) => askNovaEntries("entity", entityDraft(entity)),
  },
  {
    id: "ask-nova-image",
    kind: "image",
    getItems: (media: { src: string }, ctx) => askNovaEntries("image", imageDraft(media.src, ctx.window.location.origin)),
  },
  {
    id: "ask-nova-selection",
    kind: "selection",
    getItems: (selection: { text: string }) => askNovaEntries("selection", selectionDraft(selection.text)),
  },
]
