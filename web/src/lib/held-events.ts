/**
 * Events a discussion holds for its session's next turn (delivery with-next), by uid. History says
 * which overlays are held, a live arrival can be held, and a release says when one joined a turn.
 * A held event is in the discussion but not in the conversation yet, so it must not look read.
 */
export type HeldEvents = Readonly<Record<string, ReadonlySet<string>>>

export const HELD_EVENT_LABEL = "Held: it joins the next turn"

const NONE: ReadonlySet<string> = new Set()

function replace(prev: HeldEvents, discussionId: string, next: ReadonlySet<string>): HeldEvents {
  const current = prev[discussionId] ?? NONE
  if (current.size === next.size && [...next].every(uid => current.has(uid))) return prev
  return { ...prev, [discussionId]: next }
}

/** A history page is authoritative for the overlays it carries: held ones join, the rest leave. */
export function heldFromOverlays(
  prev: HeldEvents,
  discussionId: string,
  overlays: ReadonlyArray<{ messageUid?: string | null; held?: boolean | null }>,
): HeldEvents {
  const next = new Set(prev[discussionId] ?? NONE)
  for (const overlay of overlays) {
    if (!overlay.messageUid) continue
    if (overlay.held) next.add(overlay.messageUid)
    else next.delete(overlay.messageUid)
  }
  return replace(prev, discussionId, next)
}

export function withHeld(prev: HeldEvents, discussionId: string, uid: string): HeldEvents {
  const next = new Set(prev[discussionId] ?? NONE)
  next.add(uid)
  return replace(prev, discussionId, next)
}

export function withoutHeld(prev: HeldEvents, discussionId: string, uid: string): HeldEvents {
  const next = new Set(prev[discussionId] ?? NONE)
  next.delete(uid)
  return replace(prev, discussionId, next)
}
