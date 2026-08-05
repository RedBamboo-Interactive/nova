import type { DiscussionInfo } from "./types"

type DiscussionListUpdater = DiscussionInfo[] | ((previous: DiscussionInfo[]) => DiscussionInfo[])

let discussions: DiscussionInfo[] = []
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
  publish(applyPendingArchives(next))
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
  publish([])
}
