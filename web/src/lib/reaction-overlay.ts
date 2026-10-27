// What the person's own reaction clicks look like before the server confirms them. The base map
// only changes on the server's discussion.reaction event, which can lag a click by seconds when the
// platform is busy; without an overlay a click shows nothing until then.

export interface ReactionActor {
  id: string
  name: string
  type: "user" | "agent"
}

export interface ReactionGroup {
  emoji: string
  count: number
  actors: ReactionActor[]
  userReacted: boolean
}

export type ReactionMap = Record<string, ReactionGroup[]>

/** The person's unconfirmed changes, keyed by {@link pendingKey}. */
export type PendingReactions = Record<string, "add" | "remove">

const SEPARATOR = "\u0000"

export const PENDING_ACTOR: ReactionActor = { id: "pending:self", name: "You", type: "user" }

export function pendingKey(messageKey: string, emoji: string): string {
  return `${messageKey}${SEPARATOR}${emoji}`
}

function splitKey(key: string): [string, string] {
  const at = key.indexOf(SEPARATOR)
  return [key.slice(0, at), key.slice(at + 1)]
}

/** The base map with every pending change applied as if the server had already confirmed it. */
export function overlayReactions(base: ReactionMap, pending: PendingReactions): ReactionMap {
  const keys = Object.keys(pending)
  if (keys.length === 0) return base

  const result: ReactionMap = { ...base }
  for (const key of keys) {
    const [messageKey, emoji] = splitKey(key)
    const groups = [...(result[messageKey] ?? [])]
    const idx = groups.findIndex((g) => g.emoji === emoji)
    const group = idx >= 0 ? groups[idx] : undefined

    if (pending[key] === "add") {
      if (group?.userReacted) continue
      if (group) {
        groups[idx] = { ...group, count: group.count + 1, actors: [...group.actors, PENDING_ACTOR], userReacted: true }
      } else {
        groups.push({ emoji, count: 1, actors: [PENDING_ACTOR], userReacted: true })
      }
    } else {
      if (!group?.userReacted) continue
      // The caller's own actor is the person's, and a Nova discussion has one person.
      const drop = group.actors.map((a) => a.type).lastIndexOf("user")
      const actors = drop >= 0 ? group.actors.filter((_, i) => i !== drop) : group.actors
      if (group.count <= 1) groups.splice(idx, 1)
      else groups[idx] = { ...group, count: group.count - 1, actors, userReacted: false }
    }
    result[messageKey] = groups
  }
  return result
}

/** Pending changes the base map does not show yet; confirmed ones drop out. */
export function settlePending(base: ReactionMap, pending: PendingReactions): PendingReactions {
  let changed = false
  const next: PendingReactions = {}
  for (const [key, action] of Object.entries(pending)) {
    const [messageKey, emoji] = splitKey(key)
    const reacted = base[messageKey]?.some((g) => g.emoji === emoji && g.userReacted) ?? false
    if ((action === "add") === reacted) { changed = true; continue }
    next[key] = action
  }
  return changed ? next : pending
}
