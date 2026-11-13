import { useMemo } from "react"
import { ConversationSearchDialog, type ConversationSearchResult, type ConversationSearchSource } from "@redbamboo/chat"
import { api } from "../../lib/api"
import { createDiscussionTitleResolver } from "../../lib/discussion-display-title"
import type { DiscussionInfo } from "../../lib/types"

interface SearchSnippet {
  role: string
  timestamp: string
  snippet: string
}

interface SearchResult {
  discussionId: string
  title?: string | null
  status?: string | null
  lastActivity?: string | null
  matchCount: number
  snippets: SearchSnippet[]
  discussion?: DiscussionInfo
}

/** Nova's discussion search, shaped for the shared dialog: display titles, match counts, the first snippet. */
export function toConversationSearchResults(results: readonly SearchResult[]): ConversationSearchResult[] {
  const titleOf = createDiscussionTitleResolver(results.flatMap(r => r.discussion ? [r.discussion] : []))
  return results.map(r => ({
    id: r.discussionId,
    title: (r.discussion ? titleOf(r.discussion) : r.title) || "Untitled",
    meta: `${r.matchCount} match${r.matchCount === 1 ? "" : "es"}`,
    snippet: r.snippets[0]?.snippet,
    snippetLabel: r.snippets[0]?.role,
  }))
}

interface Props {
  onClose: () => void
  onNavigate: (discussionId: string) => void
}

export function SearchOverlay({ onClose, onNavigate }: Props) {
  const source = useMemo<ConversationSearchSource>(() => ({
    async search(query) {
      const data = await api.get<{ query: string; results: SearchResult[] }>(
        `/api/apps/nova/discussions/search?q=${encodeURIComponent(query)}&limit=20`,
      )
      return toConversationSearchResults(data.results ?? [])
    },
  }), [])
  return (
    <ConversationSearchDialog
      source={source}
      onSelect={(result) => onNavigate(result.id)}
      onClose={onClose}
      dataModal="discussion-search"
      ariaLabel="Search conversations"
      title="Search Conversations"
      placeholder="Search all discussion content…"
    />
  )
}
