import { useState, useEffect, useRef } from "react"
import { ModalBase, ModalHeader, Input } from "@redbamboo/ui"
import { api } from "../../lib/api"

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
}

interface Props {
  onClose: () => void
  onNavigate: (discussionId: string) => void
}

export function SearchOverlay({ onClose, onNavigate }: Props) {
  const [query, setQuery] = useState("")
  const [results, setResults] = useState<SearchResult[]>([])
  const [searching, setSearching] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  useEffect(() => {
    const q = query.trim()
    if (q.length < 2) {
      setResults([])
      setSearching(false)
      return
    }
    setSearching(true)
    const timer = setTimeout(async () => {
      try {
        const data = await api.get<{ query: string; results: SearchResult[] }>(
          `/api/apps/nova/discussions/search?q=${encodeURIComponent(q)}&limit=20`,
        )
        setResults(data.results ?? [])
      } catch {
        setResults([])
      } finally {
        setSearching(false)
      }
    }, 300)
    return () => clearTimeout(timer)
  }, [query])

  return (
    <ModalBase dataModal="discussion-search" ariaLabel="Search conversations" onClose={onClose} size="lg">
      <ModalHeader
        icon={<i className="ph-bold ph-magnifying-glass text-primary" />}
        title={<span className="text-sm font-medium">Search Conversations</span>}
        onClose={onClose}
      />
      <div className="px-6 pb-5 space-y-3">
        <Input
          ref={inputRef}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search all discussion content…"
        />
        <div className="max-h-80 overflow-y-auto space-y-0.5">
          {results.map((r) => (
            <button
              key={r.discussionId}
              onClick={() => onNavigate(r.discussionId)}
              className="w-full text-left px-3 py-2 rounded hover:bg-overlay-10 transition-colors"
            >
              <div className="flex items-center gap-2">
                <span className="text-sm text-contrast truncate">{r.title || "Untitled"}</span>
                <span className="text-[10px] text-text-muted shrink-0">
                  {r.matchCount} match{r.matchCount === 1 ? "" : "es"}
                </span>
              </div>
              {r.snippets[0] && (
                <div className="text-xs text-text-muted truncate mt-0.5">
                  <span className="opacity-60 mr-1">{r.snippets[0].role}:</span>
                  {r.snippets[0].snippet}
                </div>
              )}
            </button>
          ))}
          {query.trim().length >= 2 && !searching && results.length === 0 && (
            <div className="text-xs text-text-muted text-center py-6">No matches</div>
          )}
          {searching && (
            <div className="text-xs text-text-muted text-center py-6">
              <i className="ph-bold ph-spinner animate-spin mr-1.5" />
              Searching…
            </div>
          )}
        </div>
      </div>
    </ModalBase>
  )
}
