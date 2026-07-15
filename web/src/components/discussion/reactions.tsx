import { useState, useRef, useEffect } from "react"
import type { ReactionGroup } from "../../hooks/use-reactions"
import { api } from "../../lib/api"

interface ReactionEmoji {
  emoji: string
  icon: string
  color?: string
  sort: number
}

const FALLBACK: ReactionEmoji[] = [
  { emoji: "👍", icon: "ph-light ph-thumbs-up", sort: 0 },
  { emoji: "❤️", icon: "ph-bold ph-heart", color: "#ef4444", sort: 1 },
  { emoji: "🔥", icon: "ph-bold ph-fire", color: "#f59e0b", sort: 2 },
  { emoji: "👀", icon: "ph-light ph-eyes", sort: 3 },
  { emoji: "😂", icon: "ph-light ph-smiley", sort: 4 },
  { emoji: "🎉", icon: "ph-light ph-confetti", sort: 5 },
  { emoji: "💯", icon: "ph-light ph-seal-check", sort: 6 },
  { emoji: "🚀", icon: "ph-light ph-rocket", sort: 7 },
  { emoji: "✅", icon: "ph-light ph-check-circle", sort: 8 },
  { emoji: "🤔", icon: "ph-light ph-smiley-meh", sort: 9 },
  { emoji: "👏", icon: "ph-light ph-hands-clapping", sort: 10 },
  { emoji: "✨", icon: "ph-light ph-sparkle", sort: 11 },
]

let cached: ReactionEmoji[] | null = null

function useReactionEmoji(): ReactionEmoji[] {
  const [items, setItems] = useState<ReactionEmoji[]>(cached ?? FALLBACK)

  useEffect(() => {
    if (cached) return
    api.get<{ items?: any[] } | any[]>("/api/entities?type=reaction-emoji&limit=50")
      .then(res => {
        const rows: any[] = Array.isArray(res) ? res : res.items ?? []
        const list: ReactionEmoji[] = rows
          .map((e: any) => {
            const d = typeof e.data === "string" ? JSON.parse(e.data) : e.data
            return { emoji: d?.emoji ?? e.name, icon: d?.icon ?? "ph-light ph-question", color: d?.color, sort: d?.sort ?? 0 }
          })
          .sort((a: ReactionEmoji, b: ReactionEmoji) => a.sort - b.sort)
        if (list.length === 0) return
        cached = list
        setItems(list)
      })
      .catch(() => {})
  }, [])

  return items
}

function emojiIconLookup(items: ReactionEmoji[]): Map<string, ReactionEmoji> {
  const map = new Map<string, ReactionEmoji>()
  for (const item of items) map.set(item.emoji, item)
  return map
}

function ReactionIcon({ item, size = 11 }: { item: ReactionEmoji; size?: number }) {
  return <i className={item.icon} style={{ fontSize: size, ...(item.color ? { color: item.color } : undefined) }} />
}

function LookupReactionIcon({ emoji, lookup, size }: { emoji: string; lookup: Map<string, ReactionEmoji>; size?: number }) {
  const item = lookup.get(emoji)
  if (!item) return <span className="text-sm leading-none opacity-50">{emoji}</span>
  return <ReactionIcon item={item} size={size} />
}

export function ReactionPills({ reactions, onToggle }: { reactions: ReactionGroup[]; onToggle: (emoji: string, hasReacted: boolean) => void }) {
  const items = useReactionEmoji()
  const lookup = emojiIconLookup(items)

  if (reactions.length === 0) return null

  return (
    <div className="flex flex-col items-center gap-0.5 msg-enter-ai" data-visible>
      {reactions.map((r) => (
        <button
          key={r.emoji}
          onClick={() => onToggle(r.emoji, r.userReacted)}
          className={`flex flex-row items-center gap-1 h-6 px-1.5 md:flex-col md:gap-0 md:h-auto md:px-0 md:w-6 md:py-1 rounded-full text-xs transition-colors cursor-pointer ${
            r.userReacted
              ? "bg-overlay-8 text-text-secondary"
              : "bg-overlay-4 hover:bg-overlay-8 text-text-disabled hover:text-text-muted"
          }`}
          title={r.actors.map((a) => a.name).join(", ")}
        >
          <LookupReactionIcon emoji={r.emoji} lookup={lookup} />
          {r.count > 1 && <span className="font-medium tabular-nums text-[10px] leading-tight">{r.count}</span>}
        </button>
      ))}
    </div>
  )
}

export function AddReactionButton({ onAdd }: { onAdd: (emoji: string) => void }) {
  const [pickerOpen, setPickerOpen] = useState(false)
  const items = useReactionEmoji()

  return (
    <div className="relative">
      <button
        onClick={() => setPickerOpen(!pickerOpen)}
        className="w-6 h-6 flex items-center justify-center rounded-full bg-overlay-4 hover:bg-overlay-8 text-text-disabled hover:text-text-muted transition-colors cursor-pointer"
        title="Add reaction"
      >
        <i className="ph ph-smiley text-[11px]" />
      </button>
      {pickerOpen && (
        <EmojiPicker
          items={items}
          onSelect={(emoji) => { onAdd(emoji); setPickerOpen(false) }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </div>
  )
}

interface EmojiPickerProps {
  items: ReactionEmoji[]
  onSelect: (emoji: string) => void
  onClose: () => void
}

function EmojiPicker({ items, onSelect, onClose }: EmojiPickerProps) {
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose()
    }
    document.addEventListener("mousedown", handler)
    return () => document.removeEventListener("mousedown", handler)
  }, [onClose])

  return (
    <div
      ref={ref}
      className="absolute left-0 bottom-full mb-1 bg-surface-elevated rounded-lg border border-border-subtle shadow-lg p-2 z-50 msg-enter-ai"
    >
      <div className="grid grid-cols-6 gap-0.5 w-[186px]">
        {items.map((item) => (
          <button
            key={item.emoji}
            onClick={() => onSelect(item.emoji)}
            className="w-[29px] h-[29px] flex items-center justify-center rounded-md hover:bg-overlay-10 transition-colors cursor-pointer text-text-muted hover:text-text-primary"
            title={item.emoji}
          >
            <ReactionIcon item={item} size={13} />
          </button>
        ))}
      </div>
    </div>
  )
}
