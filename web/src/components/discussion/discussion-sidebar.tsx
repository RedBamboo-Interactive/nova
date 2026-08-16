import { memo, useCallback, useMemo } from "react"
import { ItemList, ItemListRow } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { DiscussionInfo, AgentInfo } from "../../lib/types"
import { resolveLiveSidebarSelection } from "../../lib/live-heartbeat"
import { isDiscussionUnread } from "../../lib/discussion-unread"

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
  onRotate: (id: string) => void
  onDismiss: (id: string) => void
  getAgent?: (id: string | null) => AgentInfo | undefined
  multiAgent?: boolean
}

export const DiscussionSidebar = memo(function DiscussionSidebar({ discussions, activeDiscussionId, onSelect, onArchive, onRotate, onDismiss, getAgent, multiAgent }: Props) {
  const sidebarSelectionId = resolveLiveSidebarSelection(discussions, activeDiscussionId)
  const { live, chat } = useMemo(() => {
    const live: DiscussionInfo[] = []
    const chat: DiscussionInfo[] = []
    for (const d of discussions) {
      if (d.type === "live") live.push(d)
      else if (d.type === "chat") chat.push(d)
    }
    return { live, chat }
  }, [discussions])

  const renderLiveItem = useCallback((discussion: DiscussionInfo) => {
    const unread = isDiscussionUnread(discussion)
    const agent = multiAgent && getAgent ? getAgent(discussion.agentId) : undefined
    return (
      <div
        data-discussion-id={discussion.id}
        data-discussion-type={discussion.type}
        data-discussion-status={discussion.status}
        data-discussion-unread={unread || undefined}
        data-discussion-selected={discussion.id === sidebarSelectionId || undefined}
      >
      <ItemListRow
        selected={discussion.id === sidebarSelectionId}
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
              <div className="absolute -bottom-1 right-0 scale-75 origin-bottom-right">
                <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
              </div>
            </>
          ) : (
            <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
          )
        }
        className={[
          agent ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
          unread ? "[&_[data-slot=item-list-title]]:font-semibold" : "",
        ].filter(Boolean).join(" ")}
        title={
          <span className="flex items-center gap-1.5">
            <i className={`ph-bold ph-broadcast text-[10px] text-status-live ${discussion.status === "thinking" ? "animate-pulse" : ""}`} />
            <span style={{ color: "var(--color-status-live)" }}>Live</span>
          </span>
        }
        subtitle={formatRelative(discussion.lastActivity)}
        trailing={
          <div className="flex items-center gap-1.5">
            {unread && (
              <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
            )}
            <button
              onClick={(e) => { e.stopPropagation(); onRotate(discussion.id) }}
              className="opacity-0 group-hover/row:opacity-100 text-text-muted hover:text-accent-teal transition-all"
              title="Rotate timeline"
            >
              <i className="ph-bold ph-arrows-clockwise text-xs" />
            </button>
          </div>
        }
      />
      </div>
    )
  }, [sidebarSelectionId, onSelect, onRotate, getAgent, multiAgent])

  const renderChatItem = useCallback((discussion: DiscussionInfo) => {
    const alive = discussion.status !== "archived" && discussion.status !== "stopped"
    const unread = alive && isDiscussionUnread(discussion)
    const agent = multiAgent && getAgent ? getAgent(discussion.agentId) : undefined
    return (
      <div
        data-discussion-id={discussion.id}
        data-discussion-type={discussion.type}
        data-discussion-status={discussion.status}
        data-discussion-unread={unread || undefined}
        data-discussion-selected={discussion.id === activeDiscussionId || undefined}
      >
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
              <div className="absolute -bottom-1 right-0 scale-75 origin-bottom-right">
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
        title={
          <>
            {discussion.title || "New discussion"}
            {discussion.confidential && <i className="ph-bold ph-lock-simple text-[10px] text-text-muted ml-1.5 opacity-60" />}
          </>
        }
        subtitle={formatRelative(discussion.lastActivity)}
        trailing={
          discussion.status !== "archived" ? (
            <div className="flex items-center gap-1.5">
              {unread && (
                <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
              )}
              <button
                onClick={(e) => { e.stopPropagation(); onArchive(discussion.id) }}
                className="opacity-0 group-hover/row:opacity-100 text-text-muted hover:text-contrast transition-all"
                title="Close discussion"
              >
                <i className="ph-bold ph-x text-xs" />
              </button>
            </div>
          ) : (
            <button
              onClick={(e) => { e.stopPropagation(); onDismiss(discussion.id) }}
              className="opacity-0 group-hover/row:opacity-100 text-text-muted hover:text-contrast transition-all"
              title="Remove from list"
            >
              <i className="ph-bold ph-x text-xs" />
            </button>
          )
        }
      />
      </div>
    )
  }, [activeDiscussionId, onSelect, onArchive, onDismiss, getAgent, multiAgent])

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {live.length > 0 && (
        <div className="shrink-0">
          <ItemList
            items={live}
            keyFn={(d) => d.id}
            renderItem={renderLiveItem}
          />
          {chat.length > 0 && (
            <div className="border-b border-overlay-6 mx-3" />
          )}
        </div>
      )}
      <div className="flex-1 overflow-hidden">
        <ItemList
          items={chat}
          keyFn={(d) => d.id}
          emptyMessage="No discussions yet"
          renderItem={renderChatItem}
        />
      </div>
    </div>
  )
})

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
