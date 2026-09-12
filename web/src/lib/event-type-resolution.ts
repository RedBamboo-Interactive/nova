import type { EventType } from "./types"

export const DEFAULT_EVENT: EventType = {
  key: "default",
  name: "Event",
  icon: "ph-bold ph-radio-button",
  color: null,
  description: null,
}

export const BUILTIN_EVENTS: Record<string, EventType> = {
  delegation: {
    key: "delegation",
    name: "Delegation",
    icon: "ph-bold ph-code",
    color: "rgb(236 72 153)",
    description: "Work delegated to a Code session",
  },
}

export function resolveEventType(
  source: string,
  lookup: ReadonlyMap<string, EventType>,
): EventType {
  const key = source.replace(/^event:/, "").split(":")[0] ?? ""
  return lookup.get(key) ?? BUILTIN_EVENTS[key] ?? DEFAULT_EVENT
}
