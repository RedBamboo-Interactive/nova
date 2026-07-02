import { useState, useRef, useEffect } from "react"
import type { ReactionGroup } from "@/hooks/use-reactions"

const QUICK_EMOJIS = ["👍", "❤️", "🔥", "👀", "😂", "🎉", "💯", "🚀", "✅", "🤔", "👏", "✨"]

interface ReactionPillsProps {
  reactions: ReactionGroup[]
  onToggle: (emoji: string, hasReacted: boolean) => void
  onAdd: (emoji: string) => void
}

export function ReactionPills({ reactions, onToggle, onAdd }: ReactionPillsProps) {
  const [pickerOpen, setPickerOpen] = useState(false)

  if (reactions.length === 0 && !pickerOpen) return null

  return (
    <div className="flex flex-wrap items-center gap-1 mt-1 msg-enter-ai">
      {reactions.map((r) => (
        <button
          key={r.emoji}
          onClick={() => onToggle(r.emoji, r.userReacted)}
          className={`inline-flex items-center gap-1 h-6 px-1.5 rounded-full text-xs transition-colors cursor-pointer ${
            r.userReacted
              ? "bg-accent-teal-a15 ring-1 ring-accent-teal-a30 text-text-primary"
              : "bg-overlay-6 hover:bg-overlay-10 text-text-muted"
          }`}
          title={r.actors.map((a) => a.name).join(", ")}
        >
          <span className="text-sm leading-none">{r.emoji}</span>
          <span className="font-medium tabular-nums">{r.count}</span>
        </button>
      ))}
      <div className="relative">
        <button
          onClick={() => setPickerOpen(!pickerOpen)}
          className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-overlay-4 hover:bg-overlay-8 text-text-disabled hover:text-text-muted transition-colors cursor-pointer"
          title="Add reaction"
        >
          <i className="fa-regular fa-face-smile text-[11px]" />
        </button>
        {pickerOpen && (
          <EmojiPicker
            onSelect={(emoji) => { onAdd(emoji); setPickerOpen(false) }}
            onClose={() => setPickerOpen(false)}
          />
        )}
      </div>
    </div>
  )
}

interface AddReactionButtonProps {
  onAdd: (emoji: string) => void
  align: "left" | "right"
}

export function AddReactionButton({ onAdd, align }: AddReactionButtonProps) {
  const [pickerOpen, setPickerOpen] = useState(false)

  return (
    <div className={`relative h-0 ${align === "right" ? "flex justify-end" : ""}`}>
      <div className={`absolute top-0 ${align === "right" ? "right-0" : "left-0"} ${pickerOpen ? "opacity-100" : "opacity-0 pointer-events-none group-hover/msg:opacity-100 group-hover/msg:pointer-events-auto"} transition-opacity duration-150`}>
        <button
          onClick={() => setPickerOpen(!pickerOpen)}
          className="mt-0.5 w-6 h-6 flex items-center justify-center rounded-full bg-overlay-6 hover:bg-overlay-10 text-text-disabled hover:text-text-muted transition-colors cursor-pointer"
          title="Add reaction"
        >
          <i className="fa-regular fa-face-smile text-[11px]" />
        </button>
        {pickerOpen && (
          <EmojiPicker
            onSelect={(emoji) => { onAdd(emoji); setPickerOpen(false) }}
            onClose={() => setPickerOpen(false)}
          />
        )}
      </div>
    </div>
  )
}

interface EmojiPickerProps {
  onSelect: (emoji: string) => void
  onClose: () => void
}

function EmojiPicker({ onSelect, onClose }: EmojiPickerProps) {
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
        {QUICK_EMOJIS.map((emoji) => (
          <button
            key={emoji}
            onClick={() => onSelect(emoji)}
            className="w-[29px] h-[29px] flex items-center justify-center rounded-md hover:bg-overlay-10 transition-colors cursor-pointer text-base"
          >
            {emoji}
          </button>
        ))}
      </div>
    </div>
  )
}
