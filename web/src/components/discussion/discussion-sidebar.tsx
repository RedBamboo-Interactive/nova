import { ItemList, ItemListRow } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { DiscussionInfo, AgentInfo } from "@/lib/types"

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
  getAgent?: (id: string | null) => AgentInfo | undefined
  multiAgent?: boolean
}

function isUnread(d: DiscussionInfo): boolean {
  return d.status === "idle" && d.messageCount > 0
    && (!d.lastReadAt || d.lastActivity > d.lastReadAt)
}

export function DiscussionSidebar({ discussions, activeDiscussionId, onSelect, onArchive, onDismiss, getAgent, multiAgent }: Props) {
  return (
    <ItemList
      items={discussions}
      keyFn={(d) => d.id}
      emptyMessage="No discussions yet"
      renderItem={(discussion) => {
        const alive = discussion.status !== "archived" && discussion.status !== "stopped"
        const unread = alive && isUnread(discussion)
        const agent = multiAgent && getAgent ? getAgent(discussion.agentId) : undefined
        return (
          <ItemListRow
            selected={discussion.id === activeDiscussionId}
            onClick={() => onSelect(discussion.id)}
            icon={
              agent ? (
                <>
                  <img
                    src={agent.avatarUrl}
                    alt={agent.name}
                    className="absolute inset-0 w-full h-full rounded-lg object-cover"
                    onError={(e) => { e.currentTarget.style.display = "none" }}
                  />
                  <div className="absolute -bottom-1 -right-1 scale-75 origin-bottom-right">
                    <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
                  </div>
                </>
              ) : (
                <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
              )
            }
            className={[
              agent ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
              !alive ? "[&_[data-slot=item-list-title]]:opacity-50"
              : unread ? "[&_[data-slot=item-list-title]]:text-contrast [&_[data-slot=item-list-title]]:font-semibold"
              : "",
            ].filter(Boolean).join(" ")}
            title={discussion.title || "New discussion"}
            subtitle={formatRelative(discussion.lastActivity)}
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
