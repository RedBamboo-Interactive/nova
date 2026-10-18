import type { DiscussionInfo } from "./types"

type DiscussionListUpdater = DiscussionInfo[] | ((previous: DiscussionInfo[]) => DiscussionInfo[])

/** Whether the server list has been read at least once. Until it has, an empty
 *  list means "not known yet", never "there are no discussions". */
export type DiscussionListLoadState = "loading" | "ready" | "failed"

let discussions: DiscussionInfo[] = []
let loadState: DiscussionListLoadState = "loading"
const listeners = new Set<() => void>()
const pendingArchiveIds = new Set<string>()

function publish(next: DiscussionInfo[]) {
  if (Object.is(next, discussions)) return
  discussions = next
  for (const listener of listeners) listener()
}

function applyPendingArchives(list: DiscussionInfo[]): DiscussionInfo[] {
  return list.map((discussion) => pendingArchiveIds.has(discussion.id)
    ? { ...discussion, status: "archiving" as const }
    : discussion)
}

function stabilize(next: DiscussionInfo[]): DiscussionInfo[] {
  const previousById = new Map(discussions.map(discussion => [discussion.id, discussion]))
  const stable = next.map(discussion => {
    const previous = previousById.get(discussion.id)
    return previous && JSON.stringify(previous) === JSON.stringify(discussion)
      ? previous
      : discussion
  })
  return stable.length === discussions.length && stable.every((discussion, index) => discussion === discussions[index])
    ? discussions
    : stable
}

/**
 * The normal Nova route and Float Nova are separate React trees, but discussion
 * records are server state shared by both. Keeping this tiny external store at
 * module scope lets both trees observe the same list while retaining their own
 * selection, transcript viewport, dialogs, and compact/standard presentation.
 */
export function getDiscussionList(): DiscussionInfo[] {
  return discussions
}

export function subscribeDiscussionList(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function setDiscussionList(update: DiscussionListUpdater): void {
  const next = typeof update === "function" ? update(discussions) : update
  publish(stabilize(applyPendingArchives(next)))
}

export function getDiscussionListLoadState(): DiscussionListLoadState {
  return loadState
}

/** Records the outcome of a server list read. Once one read has succeeded the
 *  list stays ready: a later failed refresh keeps showing the last known list. */
export function setDiscussionListLoadState(next: "ready" | "failed"): void {
  if (loadState === next || loadState === "ready") return
  loadState = next
  for (const listener of listeners) listener()
}

export function upsertDiscussion(discussion: DiscussionInfo): void {
  setDiscussionList((current) => {
    const index = current.findIndex((item) => item.id === discussion.id)
    if (index < 0) return [discussion, ...current]
    return current.map((item) => item.id === discussion.id ? discussion : item)
  })
}

export function markDiscussionArchivePending(id: string): void {
  pendingArchiveIds.add(id)
  setDiscussionList((current) => current.map((discussion) => discussion.id === id
    ? { ...discussion, status: "archiving" as const }
    : discussion))
}

export function clearDiscussionArchivePending(id: string): void {
  pendingArchiveIds.delete(id)
}

export function isDiscussionArchivePending(id: string): boolean {
  return pendingArchiveIds.has(id)
}

/** Test-only reset for the module singleton. */
export function resetDiscussionListStore(): void {
  pendingArchiveIds.clear()
  loadState = "loading"
  publish([])
}
