/**
 * Once a V2 window has a newest anchor, recovery must walk forward from that
 * anchor. Fetching only the newest page can leave an undetectable interior gap
 * when more than one page arrived while the client was away.
 */
export function historyRevalidationDirection(
  mode: "v2" | "legacy" | undefined,
  newestCursor: string | null | undefined,
): "after" | "newest" {
  return mode === "v2" && !!newestCursor ? "after" : "newest"
}

/** Reconnect clears the cache marker, but a retained V2 anchor still wins. */
export function shouldCatchUpHistory(
  mode: "v2" | "legacy" | undefined,
  newestCursor: string | null | undefined,
  wasLoaded: boolean,
): boolean {
  return wasLoaded || historyRevalidationDirection(mode, newestCursor) === "after"
}

/**
 * The first pushed overlay can race the initial V2 capability probe. Preserve
 * it until the response establishes V2 or legacy; confirmed legacy rendering
 * continues to use its existing transient-message path.
 */
export function shouldAccumulatePushedHistoryOverlay(
  mode: "v2" | "legacy" | undefined,
): boolean {
  return mode !== "legacy"
}

/** Advance, rather than delete, the tombstone observed by in-flight requests. */
export function invalidateHistoryGeneration(
  generations: Record<string, number>,
  discussionId: string,
): number {
  const generation = (generations[discussionId] ?? 0) + 1
  generations[discussionId] = generation
  return generation
}

export function isCurrentHistoryGeneration(
  generations: Record<string, number>,
  discussionId: string,
  generation: number,
): boolean {
  return generations[discussionId] === generation
}

/**
 * Lifecycle guard for work that can sit queued behind another history task.
 * A generation alone is insufficient because a queued closure allocates its
 * generation only when it eventually starts.
 */
export class HistoryLifecycleTombstones {
  private readonly retired = new Set<string>()

  retire(discussionId: string): void {
    this.retired.add(discussionId)
  }

  revive(discussionId: string): void {
    this.retired.delete(discussionId)
  }

  canRun(discussionId: string): boolean {
    return !this.retired.has(discussionId)
  }
}
