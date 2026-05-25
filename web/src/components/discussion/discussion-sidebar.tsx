import { ItemList, ItemListRow } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { DiscussionInfo } from "@/lib/types"

const statusColor: Record<string, string> = {
  thinking: "var(--color-accent-gold)",
  idle: "var(--color-accent-teal)",
  stopped: "var(--color-text-disabled)",
  archived: "var(--color-text-disabled)",
}

interface Props {
  discussions: DiscussionInfo[]
  activeDiscussionId: string | null
  onSelect: (id: string) => void
  onArchive: (id: string) => void
  onDismiss: (id: string) => void
}

function isUnread(d: DiscussionInfo): boolean {
  return d.status === "idle" && d.messageCount > 0
    && (!d.lastReadAt || d.lastActivity > d.lastReadAt)
}

export function DiscussionSidebar({ discussions, activeDiscussionId, onSelect, onArchive, onDismiss }: Props) {
  return (
    <ItemList
      items={discussions}
      keyFn={(d) => d.id}
      emptyMessage="No discussions yet"
      renderItem={(discussion) => {
        const alive = discussion.status !== "archived" && discussion.status !== "stopped"
        const unread = alive && isUnread(discussion)
        return (
          <ItemListRow
            selected={discussion.id === activeDiscussionId}
            onClick={() => onSelect(discussion.id)}
            icon={<MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />}
            title={discussion.title || "New discussion"}
            subtitle={formatRelative(discussion.lastActivity)}
            className={
              !alive ? "[&_[data-slot=item-list-title]]:opacity-50"
              : unread ? "[&_[data-slot=item-list-title]]:text-contrast [&_[data-slot=item-list-title]]:font-semibold"
              : ""
            }
            trailing={
              discussion.status !== "archived" ? (
                <div className="flex items-center gap-1.5">
                  {unread && (
                    <span className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
                  )}
                  <button
                    onClick={(e) => { e.stopPropagation(); onArchive(discussion.id) }}
                    className="opacity-0 group-hover/row:opacity-100 text-text-muted hover:text-red-400 transition-all"
                    title="Archive discussion"
                  >
                    <i className="fa-solid fa-box-archive text-xs" />
                  </button>
                </div>
              ) : (
                <button
                  onClick={(e) => { e.stopPropagation(); onDismiss(discussion.id) }}
                  className="opacity-0 group-hover/row:opacity-100 text-text-muted hover:text-contrast transition-all"
                  title="Remove from list"
                >
                  <i className="fa-solid fa-xmark text-xs" />
                </button>
              )
            }
          />
        )
      }}
    />
  )
}

function formatRelative(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime()
  const mins = Math.floor(diff / 60000)
  if (mins < 1) return "Just now"
  if (mins < 60) return `${mins}m ago`
  const hours = Math.floor(mins / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  return `${days}d ago`
}
