import { memo, useCallback, useMemo } from "react"
import type { ReactNode } from "react"
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, ItemList, ItemListRow } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { DelegationActivitySnapshot, DiscussionDelegationActivity, DiscussionInfo, AgentInfo } from "../../lib/types"
import type { DiscussionListLoadState } from "../../lib/discussion-list-store"
import { DelegationCount, DelegationSpinner } from "../delegation-activity"
import { resolveLiveSidebarSelection } from "../../lib/live-heartbeat"
import { isDiscussionUnread } from "../../lib/discussion-unread"
import { delegationAnimationPaused } from "../../lib/delegation-activity"
import { DiscussionActionItems } from "./discussion-actions-menu"

/** Row actions that act through the open discussion's header, so they open it first. */
export type DiscussionRowAction = "voice" | "info" | "rename" | "share"

const statusColor: Record<string, string> = {
  thinking: "var(--color-accent-gold)",
  idle: "var(--color-accent-teal)",
  stopped: "var(--color-text-disabled)",
  archived: "var(--color-text-disabled)",
}

function DiscussionStatusIcon({ discussion, activity }: { discussion: DiscussionInfo; activity?: DiscussionDelegationActivity }) {
  return discussion.status !== "thinking" && activity?.ongoingCount ? <DelegationSpinner paused={delegationAnimationPaused(activity)} /> : (
    <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
  )
}

/** Row actions trigger: always visible on touch screens, which have no hover.
 *  Only a hover-capable fine pointer hides it until row hover, focus or open;
 *  touch never matches a hiding rule, so stylesheet order cannot hide it there. */
function RowActionsMenu({ label, children }: { label: string; children: ReactNode }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        onClick={(e) => e.stopPropagation()}
        className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-text-muted transition-all hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 [@media(hover:hover)_and_(pointer:fine)]:opacity-0 group-hover/row:opacity-100 focus-visible:opacity-100 data-popup-open:opacity-100"
        aria-label={label}
        title={label}
      >
        <i aria-hidden="true" className="ph-bold ph-dots-three-vertical text-base" />
      </DropdownMenuTrigger>
      {/* The menu portals out of the DOM row, but React still bubbles its clicks
          to the row button; stop them so an action never also selects the row. */}
      <DropdownMenuContent align="end" sideOffset={4} onClick={(e) => e.stopPropagation()}>
        {children}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

interface Props {
  discussions: DiscussionInfo[]
  loadState: DiscussionListLoadState
  onRetryLoad: () => void
  delegationActivity: DelegationActivitySnapshot
  activeDiscussionId: string | null
  onSelect: (id: string) => void
  onArchive: (id: string) => void
  onRotate: (id: string) => void
  onDismiss: (id: string) => void
  onRowAction: (id: string, action: DiscussionRowAction) => void
  onUpdateTitle: (id: string) => void
  onFloat?: (id: string) => void
  getAgent?: (id: string | null) => AgentInfo | undefined
  multiAgent?: boolean
}

export const DiscussionSidebar = memo(function DiscussionSidebar({ discussions, loadState, onRetryLoad, delegationActivity, activeDiscussionId, onSelect, onArchive, onRotate, onDismiss, onRowAction, onUpdateTitle, onFloat, getAgent, multiAgent }: Props) {
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
          <div className="relative flex h-full w-full items-center justify-center">
            {agent ? (
              <>
                <img
                  src={agent.avatarUrl}
                  alt={agent.name}
                  className="absolute inset-0 w-full h-full rounded-lg object-cover"
                  onError={(e) => { e.currentTarget.style.display = "none" }}
                />
                <div className="absolute -bottom-1 right-0 origin-bottom-right scale-75">
                  <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} />
                </div>
              </>
            ) : (
              <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} />
            )}
          </div>
        }
        className={[
          agent || delegationActivity[discussion.id]?.ongoingCount ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
          unread ? "[&_[data-slot=item-list-title]]:font-semibold" : "",
        ].filter(Boolean).join(" ")}
        title={
          <span className="flex items-center gap-1.5">
            <i className={`ph-bold ph-broadcast text-[10px] text-status-live ${discussion.status === "thinking" ? "animate-pulse" : ""}`} />
            <span style={{ color: "var(--color-status-live)" }}>Live</span>
          </span>
        }
        subtitle={<span className="inline-flex items-baseline gap-1.5"><span>{formatRelative(discussion.lastActivity)}</span><DelegationCount activity={delegationActivity[discussion.id]} /></span>}
        trailing={
          <div className="flex items-center gap-1.5">
            {unread && (
              <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
            )}
            <RowActionsMenu label="Live actions">
              <DiscussionActionItems
                showShortcuts={discussion.id === activeDiscussionId}
                onVoice={() => onRowAction(discussion.id, "voice")}
                voiceDisabled={discussion.status === "stopped"}
                onInfo={() => onRowAction(discussion.id, "info")}
                onShare={!discussion.confidential ? () => onRowAction(discussion.id, "share") : undefined}
                onFloat={onFloat ? () => onFloat(discussion.id) : undefined}
                onReset={() => onRotate(discussion.id)}
              />
            </RowActionsMenu>
          </div>
        }
      />
      </div>
    )
  }, [sidebarSelectionId, activeDiscussionId, onSelect, onRotate, onRowAction, onFloat, getAgent, multiAgent, delegationActivity])

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
          <div className="relative flex h-full w-full items-center justify-center">
            {agent ? (
              <>
                <img
                  src={agent.avatarUrl}
                  alt={agent.name}
                  className="absolute inset-0 w-full h-full rounded-lg object-cover"
                  onError={(e) => { e.currentTarget.style.display = "none" }}
                />
                <div className="absolute -bottom-1 right-0 origin-bottom-right scale-75">
                  <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} />
                </div>
              </>
            ) : (
              <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} />
            )}
          </div>
        }
        className={[
          agent || delegationActivity[discussion.id]?.ongoingCount ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
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
        subtitle={<span className="inline-flex items-baseline gap-1.5"><span>{formatRelative(discussion.lastActivity)}</span><DelegationCount activity={delegationActivity[discussion.id]} /></span>}
        trailing={
          discussion.status !== "archived" ? (
            <div className="flex items-center gap-1.5">
              {unread && (
                <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
              )}
              <RowActionsMenu label="Conversation actions">
                <DiscussionActionItems
                  showShortcuts={discussion.id === activeDiscussionId}
                  onVoice={() => onRowAction(discussion.id, "voice")}
                  voiceDisabled={discussion.status === "stopped"}
                  onInfo={() => onRowAction(discussion.id, "info")}
                  onRename={() => onRowAction(discussion.id, "rename")}
                  onUpdateTitle={() => onUpdateTitle(discussion.id)}
                  onShare={!discussion.confidential ? () => onRowAction(discussion.id, "share") : undefined}
                  onFloat={onFloat ? () => onFloat(discussion.id) : undefined}
                  onClose={() => onArchive(discussion.id)}
                />
              </RowActionsMenu>
            </div>
          ) : (
            <RowActionsMenu label="Conversation actions">
              <DropdownMenuItem onClick={() => onDismiss(discussion.id)}>
                <i aria-hidden="true" className="ph-bold ph-x size-4" />
                Remove from list
              </DropdownMenuItem>
            </RowActionsMenu>
          )
        }
      />
      </div>
    )
  }, [activeDiscussionId, onSelect, onArchive, onDismiss, onRowAction, onUpdateTitle, onFloat, getAgent, multiAgent, delegationActivity])

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
        {discussions.length === 0 && loadState === "loading" ? (
          <p data-discussion-list-state="loading" className="text-text-muted text-sm p-4 text-center">
            <i aria-hidden="true" className="ph-bold ph-spinner animate-spin mr-1.5" />
            Loading discussions…
          </p>
        ) : discussions.length === 0 && loadState === "failed" ? (
          <div data-discussion-list-state="failed" className="text-text-muted text-sm p-4 text-center">
            <p className="mb-3">Couldn't load discussions</p>
            <button
              onClick={onRetryLoad}
              className="text-xs px-3 py-1.5 rounded bg-overlay-10 hover:bg-overlay-15 text-contrast transition-colors"
            >
              <i aria-hidden="true" className="ph-bold ph-arrow-clockwise mr-1.5" />
              Retry
            </button>
          </div>
        ) : (
          <ItemList
            items={chat}
            keyFn={(d) => d.id}
            emptyMessage="No discussions yet"
            renderItem={renderChatItem}
          />
        )}
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
