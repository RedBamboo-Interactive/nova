import { FRIEZE_TOOL_CATEGORIES, friezeMarkerKey, type FriezeMarkerVisibility, type FriezeToolCategory } from "@redbamboo/chat/frieze-markers"
import { BUILTIN_EVENTS } from "./event-type-resolution.ts"
import type { EventType } from "./types"

/**
 * Which frieze markers (event dots, tool and thinking squares) the viewer hides,
 * separately for the Live discussion and for every other discussion. Stored as
 * hidden keys, so a kind nobody has seen yet (a new event type) shows.
 * A viewer preference only: nothing about the event or the transcript changes.
 */
export interface HiddenFriezeMarkers {
  live: string[]
  discussions: string[]
}

export type FriezeMarkerScope = keyof HiddenFriezeMarkers

export const NO_HIDDEN_FRIEZE_MARKERS: HiddenFriezeMarkers = { live: [], discussions: [] }

export function friezeMarkerScope(discussionType: string | null | undefined): FriezeMarkerScope {
  return discussionType === "live" ? "live" : "discussions"
}

/** Malformed or missing storage reads as "nothing hidden". */
export function normalizeHiddenFriezeMarkers(value: unknown): HiddenFriezeMarkers {
  const read = (list: unknown) => Array.isArray(list) ? list.filter((key): key is string => typeof key === "string") : []
  const record = value && typeof value === "object" ? value as Record<string, unknown> : {}
  return { live: read(record.live), discussions: read(record.discussions) }
}

/**
 * The predicate handed to ChatPanel, or undefined when nothing is hidden so the
 * panel takes its unfiltered path. Shared Chat still shows a hidden marker that
 * needs attention (failed delivery, in flight, awaiting approval).
 */
export function friezeMarkerVisibility(hidden: readonly string[]): FriezeMarkerVisibility | undefined {
  if (!hidden.length) return undefined
  const keys = new Set(hidden)
  return part => {
    const key = friezeMarkerKey(part)
    return !key || !keys.has(key)
  }
}

export function setFriezeMarkerShown(
  current: HiddenFriezeMarkers,
  scope: FriezeMarkerScope,
  key: string,
  shown: boolean,
): HiddenFriezeMarkers {
  const without = current[scope].filter(candidate => candidate !== key)
  return { ...current, [scope]: shown ? without : [...without, key] }
}

export interface FriezeMarkerSettingRow {
  key: string
  name: string
  color: string | null
  /** Event dots are round; everything else is a square. */
  shape: "dot" | "square"
}

const TOOL_ROWS: Record<FriezeToolCategory, { name: string; color: string }> = {
  "read-only": { name: "Reads and searches", color: "var(--color-accent-teal)" },
  mutating: { name: "Edits and writes", color: "var(--color-accent-gold)" },
  shell: { name: "Shell commands", color: "var(--color-accent-gold)" },
  agent: { name: "Agents and delegations", color: "rgb(236 72 153)" },
  other: { name: "Other tools", color: "var(--color-text-disabled)" },
}

/** Event kinds that keep the square tool affordance (see shared `usesSquareEventMarker`). */
const SQUARE_EVENTS = new Set(["delegation", "voice-session"])

/** Registered event types first-hand from the API, plus Nova's built-ins it doesn't list, by name. */
export function eventMarkerRows(types: readonly EventType[]): FriezeMarkerSettingRow[] {
  const byKey = new Map<string, EventType>()
  for (const type of types) if (type.key && !byKey.has(type.key)) byKey.set(type.key, type)
  for (const type of Object.values(BUILTIN_EVENTS)) if (!byKey.has(type.key)) byKey.set(type.key, type)
  return [...byKey.values()]
    .sort((a, b) => a.name.localeCompare(b.name))
    .map(type => ({
      key: `event:${type.key}`,
      name: type.name,
      color: type.color,
      shape: SQUARE_EVENTS.has(type.key) ? "square" : "dot",
    }))
}

export function activityMarkerRows(): FriezeMarkerSettingRow[] {
  return [
    ...FRIEZE_TOOL_CATEGORIES.map(category => ({ key: `tool:${category}`, ...TOOL_ROWS[category], shape: "square" as const })),
    { key: "thinking", name: "Thinking", color: "var(--color-domain-imagination, #7C4DFF)", shape: "square" },
  ]
}
