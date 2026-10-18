import type { DiscussionInfo } from "./types"

export interface ArchiveDiscussionItem extends DiscussionInfo {
  searchMatch?: "title" | "content"
  searchSnippet?: string | null
  matchCount?: number
}

export interface ArchiveContentResult {
  discussionId: string
  matchCount: number
  snippets: Array<{ role: string; timestamp: string; snippet: string }>
  discussion?: DiscussionInfo
}

export type ArchiveListLoadState = "loading" | "ready" | "failed"

export interface ArchiveDiscussionState {
  enabled: boolean
  items: ArchiveDiscussionItem[]
  knownItems: ArchiveDiscussionItem[]
  loadState: ArchiveListLoadState
  hasLoaded: boolean
  query: string
  agentId: string | null
  cursor: string | null
  hasMore: boolean
}

let state: ArchiveDiscussionState = {
  enabled: false,
  items: [],
  knownItems: [],
  loadState: "loading",
  hasLoaded: false,
  query: "",
  agentId: null,
  cursor: null,
  hasMore: false,
}
const listeners = new Set<() => void>()

function publish(next: ArchiveDiscussionState) {
  state = next
  for (const listener of listeners) listener()
}

export function getArchiveDiscussionState(): ArchiveDiscussionState {
  return state
}

export function subscribeArchiveDiscussionState(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function setArchiveMode(enabled: boolean): void {
  if (state.enabled === enabled) return
  publish({ ...state, enabled })
}

export function setArchiveQuery(query: string): void {
  if (state.query === query) return
  publish({
    ...state,
    query,
    items: [],
    cursor: null,
    hasMore: false,
    hasLoaded: false,
    loadState: "loading",
  })
}

export function beginArchiveLoad(query: string, append: boolean, agentId: string | null = null): void {
  const sameScope = state.query === query && state.agentId === agentId
  const preserveKnown = sameScope && state.hasLoaded
  publish({
    ...state,
    query,
    agentId,
    items: append || preserveKnown ? state.items : [],
    cursor: append && sameScope ? state.cursor : null,
    hasMore: append && sameScope ? state.hasMore : false,
    hasLoaded: sameScope ? state.hasLoaded : false,
    loadState: "loading",
  })
}

export function completeArchiveLoad(
  items: ArchiveDiscussionItem[],
  cursor: string | null,
  hasMore: boolean,
  query: string,
  agentId: string | null,
  append: boolean,
): void {
  // A slower prior query must never overwrite a newer search.
  if (state.query !== query || state.agentId !== agentId) return
  const merged = append
    ? deduplicateArchiveItems([...state.items, ...items])
    : deduplicateArchiveItems(items)
  publish({
    ...state,
    items: merged,
    knownItems: deduplicateArchiveItems([...merged, ...state.knownItems]),
    cursor,
    hasMore,
    loadState: "ready",
    hasLoaded: true,
  })
}

export function failArchiveLoad(query: string, agentId: string | null = null): void {
  if (state.query !== query || state.agentId !== agentId) return
  publish({ ...state, loadState: state.hasLoaded ? "ready" : "failed" })
}

export function upsertArchivedDiscussion(discussion: DiscussionInfo): void {
  const listed = state.items.find(item => item.id === discussion.id)
  const items = listed
    ? state.items.map(item => item.id === discussion.id ? { ...item, ...discussion } : item)
    : state.items
  const known = state.knownItems.find(item => item.id === discussion.id)
  const knownItems = known
    ? state.knownItems.map(item => item.id === discussion.id ? { ...item, ...discussion } : item)
    : [discussion, ...state.knownItems]
  publish({ ...state, items, knownItems })
}

export function removeArchivedDiscussion(id: string): void {
  const items = state.items.filter(item => item.id !== id)
  const knownItems = state.knownItems.filter(item => item.id !== id)
  if (items.length !== state.items.length || knownItems.length !== state.knownItems.length)
    publish({ ...state, items, knownItems })
}

export function mergeArchivedSearchResults(
  titleMatches: DiscussionInfo[],
  contentMatches: ArchiveContentResult[],
): ArchiveDiscussionItem[] {
  const titleIds = new Set(titleMatches.map(item => item.id))
  const titles: ArchiveDiscussionItem[] = titleMatches.map(item => ({
    ...item,
    searchMatch: "title",
  }))
  const content: ArchiveDiscussionItem[] = []
  for (const result of contentMatches) {
    if (titleIds.has(result.discussionId) || !result.discussion) continue
    content.push({
      ...result.discussion,
      searchMatch: "content",
      searchSnippet: result.snippets[0]?.snippet ?? null,
      matchCount: result.matchCount,
    })
  }
  return deduplicateArchiveItems([...titles, ...content])
}

export function createArchiveContentSearchParams(
  query: string,
  agentId: string | null,
): URLSearchParams {
  const search = new URLSearchParams({
    q: query,
    limit: "100",
    status: "archived",
    includeLive: "true",
    includeHeartbeats: "false",
  })
  if (agentId) search.set("agent", agentId)
  return search
}

export function resolveArchiveSearchEscape(query: string): "clear" | "exit" {
  return query.length > 0 ? "clear" : "exit"
}

export function resolveKnownDiscussion(
  open: DiscussionInfo[],
  archived: DiscussionInfo[],
  id: string,
): DiscussionInfo | undefined {
  return open.find(item => item.id === id) ?? archived.find(item => item.id === id)
}

export function canCommitDiscussionHistory(
  discussion: DiscussionInfo | undefined,
  archivePending: boolean,
  expectedSessionId?: string | null,
): boolean {
  return !!discussion
    && !archivePending
    && (expectedSessionId === undefined || discussion.sessionId === expectedSessionId)
}

function deduplicateArchiveItems(items: ArchiveDiscussionItem[]): ArchiveDiscussionItem[] {
  const seen = new Set<string>()
  return items.filter(item => {
    if (seen.has(item.id)) return false
    seen.add(item.id)
    return true
  })
}

/** Test-only reset for the session-scoped singleton. */
export function resetArchiveDiscussionStore(): void {
  publish({
    enabled: false,
    items: [],
    knownItems: [],
    loadState: "loading",
    hasLoaded: false,
    query: "",
    agentId: null,
    cursor: null,
    hasMore: false,
  })
}
