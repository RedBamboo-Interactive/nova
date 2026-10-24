import type { EventType } from "./types"
import { AGENT_NOTE_KEY, agentNoteLabel, parseAgentNoteSource } from "./agent-note.ts"

export const DEFAULT_EVENT: EventType = {
  key: "default",
  name: "Event",
  icon: "ph-bold ph-radio-button",
  color: null,
  description: null,
}

export const BUILTIN_EVENTS: Record<string, EventType> = {
  delegate: {
    key: "delegate",
    name: "Delegation update",
    icon: "ph-bold ph-code",
    color: "rgb(236 72 153)",
    description: "Status updates from delegated Code sessions",
  },
  delegation: {
    key: "delegation",
    name: "Delegation",
    icon: "ph-bold ph-code",
    color: "rgb(236 72 153)",
    description: "Work delegated to a Code session",
  },
  [AGENT_NOTE_KEY]: {
    key: AGENT_NOTE_KEY,
    name: "Agent note",
    icon: "ph-bold ph-chats-circle",
    color: "rgb(236 72 153)",
    description: "Notes another Agent instance posted into this discussion",
  },
}

export function resolveEventType(
  source: string,
  lookup: ReadonlyMap<string, EventType>,
): EventType {
  const key = source.replace(/^event:/, "").split(":")[0] ?? ""
  const known = lookup.get(key) ?? BUILTIN_EVENTS[key]
  if (known) return known
  // Instance notes arrive under the sender's own `<agent>-<discussionId>`
  // source; they share one presentation, named after the sender.
  const sender = parseAgentNoteSource(key)
  if (sender) {
    const note = lookup.get(AGENT_NOTE_KEY) ?? BUILTIN_EVENTS[AGENT_NOTE_KEY]!
    return { ...note, name: agentNoteLabel(sender.agent, sender.discussionId) }
  }
  return DEFAULT_EVENT
}
