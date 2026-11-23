import type { SessionInfoMemory, SessionInfoMood, SessionInfoSkill, SessionInfoSkills } from "@redbamboo/chat"

/**
 * What the Info modal's Skills, Memory and Workspace tabs show for a Nova agent, read from the
 * agent's entities and its workspace. Nothing here is invented: a value the sources lack is left out.
 */

/** The notes an agent reads at the start of a conversation: the "At conversation start" section of its generated CLAUDE.md. */
export const MEMORY_START_NOTES: readonly { path: string; label: string }[] = [
  { path: "memory/index.md", label: "Startup map" },
  { path: "memory/meta/feedback_style.md", label: "Operating rules" },
  { path: "memory/dreaming/texture/texture.md", label: "Emotional cues" },
  { path: "memory/dreaming/mood.md", label: "Today's mood" },
]

export const MOOD_PATH = "memory/dreaming/mood.md"

/** The kernel returns an entity's `data` as an object on one route and as a JSON string on another. */
export function entityData(entity: { data?: unknown } | null | undefined): Record<string, unknown> {
  const data = entity?.data
  if (typeof data === "string") {
    try {
      const parsed: unknown = JSON.parse(data)
      return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {}
    } catch {
      return {}
    }
  }
  return data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : {}
}

export interface EntityLike {
  id: string
  name?: string
  slug?: string
  data?: unknown
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null
}

/** The skills an agent has, in the agent's own order. Ids with no skill entity are skipped. */
export function agentSkills(agent: EntityLike | null | undefined, skillEntities: readonly EntityLike[]): SessionInfoSkills["items"] {
  const ids = entityData(agent).skills
  if (!Array.isArray(ids)) return []
  const byId = new Map(skillEntities.map(entity => [entity.id, entity]))
  const items: SessionInfoSkill[] = []
  for (const id of ids) {
    const entity = typeof id === "string" ? byId.get(id) : undefined
    const name = text(entity?.name)
    if (!entity || !name) continue
    const data = entityData(entity)
    items.push({ id: entity.id, name, slug: text(entity.slug) ?? undefined, icon: text(data.icon), description: text(data.description) })
  }
  return items
}

const HEX_COLOR = /#(?:[0-9a-f]{6}|[0-9a-f]{3})\b/i

/**
 * Today's mood from `memory/dreaming/mood.md`: its Energy, Vibe and Color lines. A swatch colour is
 * given only when the Color line carries a real hex value; a colour name alone draws no swatch.
 */
export function parseMood(markdown: string | null | undefined): SessionInfoMood | null {
  if (!markdown) return null
  const field = (name: string) => text(markdown.match(new RegExp(`^\s*${name}\s*:\s*(.+?)\s*$`, "im"))?.[1])
  const energy = field("Energy")
  const vibe = field("Vibe")
  const color = field("Color")
  const carrying = markdown.match(/^##\s+What I'm carrying\s*\r?\n+([\s\S]*?)(?=\r?\n##\s|$)/im)?.[1]
  const firstSentence = carrying?.trim().split(/\r?\n\r?\n/)[0]?.replace(/\s+/g, " ").match(/^.*?[.!?](?=\s|$)/)?.[0] ?? null
  if (!energy && !vibe && !color) return null
  const hex = color?.match(HEX_COLOR)?.[0] ?? null
  return {
    energy,
    vibe,
    colorName: color ? color.replace(HEX_COLOR, "").replace(/[()\s]+$/g, "").trim() || null : null,
    colorValue: hex,
    note: firstSentence,
  }
}

export function agentMemory(mood: SessionInfoMood | null, workspaceHref: string | null): SessionInfoMemory {
  return {
    mood,
    readAtStart: MEMORY_START_NOTES.map(note => ({ path: note.path, label: note.label })),
    root: "memory/",
    workspaceHref,
    workspaceLabel: "Open memory in the workspace",
  }
}
