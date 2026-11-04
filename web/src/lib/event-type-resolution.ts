import type { EventType } from "./types"
import { AGENT_NOTE_KEY, AGENT_NOTE_SENT_KEY, agentNoteLabel, parseAgentNoteSource } from "./agent-note.ts"

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
  [AGENT_NOTE_SENT_KEY]: {
    key: AGENT_NOTE_SENT_KEY,
    name: "Note sent",
    icon: "ph-bold ph-paper-plane-tilt",
    color: "rgb(236 72 153)",
    description: "What this Agent sent to another discussion",
  },
  heartbeat: {
    key: "heartbeat",
    name: "Heartbeat",
    icon: "ph-bold ph-heartbeat",
    color: "#C74B7A",
    description: "What the Heartbeat noticed and passed on",
  },
  "heartbeat-tick": {
    key: "heartbeat-tick",
    name: "Heartbeat tick",
    icon: "ph-bold ph-timer",
    color: "#5B8BC4",
    description: "The Heartbeat's scheduled look around",
  },
  "morning-greeting": {
    key: "morning-greeting",
    name: "Morning greeting",
    icon: "ph-bold ph-coffee",
    color: "#C9944A",
    description: "The invitation to say good morning",
  },
  spark: {
    key: "spark",
    name: "Spark",
    icon: "ph-bold ph-sparkle",
    color: "#C9944A",
    description: "An invitation to bring something up",
  },
  "discord-reply": {
    key: "discord-reply",
    name: "Discord reply",
    icon: "ph-bold ph-discord-logo",
    color: "#7289DA",
    description: "Someone answered on Discord after this discussion wrote there",
  },
  "event-queue": {
    key: "event-queue",
    name: "Event queue",
    icon: "ph-bold ph-tray",
    color: "#5B8BC4",
    description: "Events that couldn't be delivered to this discussion",
  },
}

/** Presentation of a source nobody registered: named after itself, never anonymous. */
const UNREGISTERED_EVENT = { icon: "ph-bold ph-broadcast", color: "#5B8BC4" }

/** `josie-handoff` reads "Josie handoff". A bare id says nothing, so it isn't echoed. */
export function humanizeEventSource(key: string): string {
  if (/^[0-9a-f]{8,}$/i.test(key)) return "From another discussion"
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[\s_-]+/)
    .filter(Boolean)
    .join(" ")
    .toLowerCase()
  return words ? `${words.charAt(0).toUpperCase()}${words.slice(1)}` : DEFAULT_EVENT.name
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
  return { ...DEFAULT_EVENT, ...UNREGISTERED_EVENT, name: humanizeEventSource(key) }
}

/** True when nobody registered this source: its name is only a humanised key. */
export function isUnregisteredEvent(event: EventType): boolean {
  return event.key === DEFAULT_EVENT.key
}
