import { useState, useRef, useEffect } from "react"
import type { ReactionGroup } from "@/hooks/use-reactions"

interface ReactionEmoji {
  emoji: string
  icon: string
  color?: string
  sort: number
}

const FALLBACK: ReactionEmoji[] = [
  { emoji: "👍", icon: "fa-light fa-thumbs-up", sort: 0 },
  { emoji: "❤️", icon: "fa-solid fa-heart", color: "#ef4444", sort: 1 },
  { emoji: "🔥", icon: "fa-solid fa-fire-flame-curved", color: "#f59e0b", sort: 2 },
  { emoji: "👀", icon: "fa-light fa-eyes", sort: 3 },
  { emoji: "😂", icon: "fa-light fa-face-laugh-squint", sort: 4 },
  { emoji: "🎉", icon: "fa-light fa-party-horn", sort: 5 },
  { emoji: "💯", icon: "fa-light fa-hundred-points", sort: 6 },
  { emoji: "🚀", icon: "fa-light fa-rocket", sort: 7 },
  { emoji: "✅", icon: "fa-light fa-circle-check", sort: 8 },
  { emoji: "🤔", icon: "fa-light fa-face-thinking", sort: 9 },
  { emoji: "👏", icon: "fa-light fa-hands-clapping", sort: 10 },
  { emoji: "✨", icon: "fa-light fa-sparkles", sort: 11 },
]

let cached: ReactionEmoji[] | null = null

function useReactionEmoji(): ReactionEmoji[] {
  const [items, setItems] = useState<ReactionEmoji[]>(cached ?? FALLBACK)

  useEffect(() => {
    if (cached) return
    fetch("http://localhost:18804/api/entities?type=reaction-emoji&limit=50")
      .then(r => r.json())
      .then(res => {
        const list: ReactionEmoji[] = (res.items ?? res)
          .map((e: any) => {
            const d = typeof e.data === "string" ? JSON.parse(e.data) : e.data
            return { emoji: d?.emoji ?? e.name, icon: d?.icon ?? "fa-light fa-circle-question", color: d?.color, sort: d?.sort ?? 0 }
          })
          .sort((a: ReactionEmoji, b: ReactionEmoji) => a.sort - b.sort)
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

function ReactionIcon({ emoji, lookup, size = 11 }: { emoji: string; lookup: Map<string, ReactionEmoji>; size?: number }) {
  const item = lookup.get(emoji)
  if (!item) return <span className="text-sm leading-none opacity-50">{emoji}</span>
  return <i className={`${item.icon} text-[${size}px]`} style={item.color ? { color: item.color } : undefined} />
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
          className={`flex flex-col items-center w-6 py-1 rounded-full text-xs transition-colors cursor-pointer ${
            r.userReacted
              ? "bg-overlay-8 text-text-secondary"
              : "bg-overlay-4 hover:bg-overlay-8 text-text-disabled hover:text-text-muted"
          }`}
          title={r.actors.map((a) => a.name).join(", ")}
        >
          <ReactionIcon emoji={r.emoji} lookup={lookup} />
          <span className="font-medium tabular-nums text-[10px] leading-tight">{r.count}</span>
        </button>
      ))}
    </div>
  )
}

export function AddReactionButton({ onAdd }: { onAdd: (emoji: string) => void }) {
  const [pickerOpen, setPickerOpen] = useState(false)
  const items = useReactionEmoji()
  const lookup = emojiIconLookup(items)

  return (
    <div className="relative">
      <button
        onClick={() => setPickerOpen(!pickerOpen)}
        className="w-6 h-6 flex items-center justify-center rounded-full bg-overlay-4 hover:bg-overlay-8 text-text-disabled hover:text-text-muted transition-colors cursor-pointer"
        title="Add reaction"
      >
        <i className="fa-regular fa-face-smile text-[11px]" />
      </button>
      {pickerOpen && (
        <EmojiPicker
          items={items}
          lookup={lookup}
          onSelect={(emoji) => { onAdd(emoji); setPickerOpen(false) }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </div>
  )
}

interface EmojiPickerProps {
  items: ReactionEmoji[]
  lookup: Map<string, ReactionEmoji>
  onSelect: (emoji: string) => void
  onClose: () => void
}

function EmojiPicker({ items, lookup, onSelect, onClose }: EmojiPickerProps) {
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
            <ReactionIcon emoji={item.emoji} lookup={lookup} size={13} />
          </button>
        ))}
      </div>
    </div>
  )
}
