import type { DiscussionInfo } from "./types"

/**
 * Finds a discussion the client doesn't list, an archived one typically, by its id. One read per id
 * for the page's life. A refusal or a missing discussion is remembered (it has no title to show);
 * any other failure is forgotten, so the next caller tries again.
 */
export type DiscussionLookup = (id: string) => Promise<DiscussionInfo | null>

export function createDiscussionLookup(read: (id: string) => Promise<DiscussionInfo>): DiscussionLookup {
  const cache = new Map<string, Promise<DiscussionInfo | null>>()
  return (id) => {
    const hit = cache.get(id)
    if (hit) return hit
    const lookup = read(id).catch((error: unknown) => {
      const status = (error as { status?: unknown } | null)?.status
      if (!(typeof status === "number" && status >= 400 && status < 500)) cache.delete(id)
      return null
    })
    cache.set(id, lookup)
    return lookup
  }
}

/** The discussions a report names that the client doesn't list, each once, in order. */
export function unlistedIds(ids: Iterable<string>, listed: (id: string) => boolean): string[] {
  const out: string[] = []
  for (const id of ids) if (id && !listed(id) && !out.includes(id)) out.push(id)
  return out
}
