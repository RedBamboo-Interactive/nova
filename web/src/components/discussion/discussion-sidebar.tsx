import { memo, useCallback, useMemo } from "react"
import type { ReactNode } from "react"
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, ItemList, ItemListRow } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { BackgroundTaskInfo } from "@redbamboo/chat"
import type { DelegationActivitySnapshot, DiscussionDelegationActivity, DiscussionInfo, AgentInfo } from "../../lib/types"
import type { DiscussionListLoadState } from "../../lib/discussion-list-store"
import { resolveArchiveSearchEscape, type ArchiveDiscussionItem } from "../../lib/archive-discussion-store"

import { resolveLiveSidebarSelection } from "../../lib/live-heartbeat"
import { isDiscussionUnread } from "../../lib/discussion-unread"
import { discussionActivityIndicator } from "../../lib/delegation-activity"
import { DiscussionActionItems } from "./discussion-actions-menu"
import { LiveQuotaRing } from "./live-quota-ring"
import { formatOwningApp } from "../../lib/discussion-owner"
import { createDiscussionTitleResolver } from "../../lib/discussion-display-title"

/** Row actions that act through the open discussion's header, so they open it first. */
export type DiscussionRowAction = "voice" | "info" | "artifacts" | "rename" | "share"

const statusColor: Record<string, string> = {
  thinking: "var(--color-accent-gold)",
  idle: "var(--color-accent-teal)",
  stopped: "var(--color-text-disabled)",
  archived: "var(--color-text-disabled)",
}

// Background work takes the chat activity row's colours: agents and
// delegations pink, shells and other background tasks gold.
const agentColor = "rgb(236 72 153)"
const delegationColor = `var(--color-accent-magenta, ${agentColor})`
const taskColor = "var(--color-accent-gold)"

/** Ongoing delegations or running background tasks: work behind an otherwise idle discussion. */
function hasBackgroundWork(discussion: DiscussionInfo, activity: DiscussionDelegationActivity | undefined, backgroundTasks: readonly BackgroundTaskInfo[] | undefined): boolean {
  const source = discussionActivityIndicator(discussion.status, activity, backgroundTasks).source
  return source !== "none" && source !== "session"
}

/**
 * One indicator per discussion, chosen by the chat activity row's priority:
 * the session working, then its agents, then other background tasks, then
 * its delegations, then its idle or stopped state.
 */
function DiscussionStatusIcon({ discussion, activity, backgroundTasks }: { discussion: DiscussionInfo; activity?: DiscussionDelegationActivity; backgroundTasks?: readonly BackgroundTaskInfo[] }) {
  const indicator = discussionActivityIndicator(discussion.status, activity, backgroundTasks)
  if (indicator.source === "session" || indicator.source === "none") return (
    <MorphSpinner color={statusColor[discussion.status] || "var(--color-text-disabled)"} paused={discussion.status !== "thinking"} />
  )
  const color = indicator.source === "agents" ? agentColor : indicator.source === "delegations" ? delegationColor : taskColor
  return (
    <span data-slot="discussion-activity" data-activity-source={indicator.source} role="img" aria-label={indicator.label} title={indicator.label} className="inline-flex shrink-0">
      <MorphSpinner color={color} paused={indicator.paused} />
    </span>
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
  /** Live background tasks (subagents, shells) per discussion id. */
  backgroundTasks: Record<string, BackgroundTaskInfo[]>
  activeDiscussionId: string | null
  onSelect: (id: string) => void
  onArchive: (id: string) => void
  onRotate: (id: string) => void
  onDismiss: (id: string) => void
  onRowAction: (id: string, action: DiscussionRowAction) => void
  onUpdateTitle: (id: string) => void
  onFloat?: (id: string) => void
  /** Stops a running chat's session process; the next send resumes it. */
  onStop?: (id: string) => void
  getAgent?: (id: string | null) => AgentInfo | undefined
  multiAgent?: boolean
  archiveMode?: boolean
  archiveQuery?: string
  archiveHasMore?: boolean
  onArchiveQueryChange?: (query: string) => void
  onExitArchiveMode?: () => void
  onLoadMoreArchived?: () => void
}

export const DiscussionSidebar = memo(function DiscussionSidebar({ discussions, loadState, onRetryLoad, delegationActivity, backgroundTasks, activeDiscussionId, onSelect, onArchive, onRotate, onDismiss, onRowAction, onUpdateTitle, onFloat, onStop, getAgent, multiAgent, archiveMode = false, archiveQuery = "", archiveHasMore = false, onArchiveQueryChange, onExitArchiveMode, onLoadMoreArchived }: Props) {
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
  const titleOf = useMemo(() => createDiscussionTitleResolver(discussions), [discussions])

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
                  <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} />
                </div>
              </>
            ) : (
              <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} />
            )}
          </div>
        }
        className={[
          agent || hasBackgroundWork(discussion, delegationActivity[discussion.id], backgroundTasks[discussion.id]) ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
          unread ? "[&_[data-slot=item-list-title]]:font-semibold" : "",
        ].filter(Boolean).join(" ")}
        title={
          <span className="flex items-center gap-1.5">
            <LiveQuotaRing discussion={discussion} size={16}>
              <i className={`ph-bold ph-broadcast text-[10px] text-status-live ${discussion.status === "thinking" ? "animate-pulse" : ""}`} />
            </LiveQuotaRing>
            <span style={{ color: "var(--color-status-live)" }}>Live</span>
          </span>
        }
        subtitle={formatRelative(discussion.lastActivity)}
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
                onArtifacts={() => onRowAction(discussion.id, "artifacts")}
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
  }, [sidebarSelectionId, activeDiscussionId, onSelect, onRotate, onRowAction, onFloat, getAgent, multiAgent, delegationActivity, backgroundTasks])

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
                  <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} />
                </div>
              </>
            ) : (
              <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} />
            )}
          </div>
        }
        className={[
          agent || hasBackgroundWork(discussion, delegationActivity[discussion.id], backgroundTasks[discussion.id]) ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
          !alive ? "[&_[data-slot=item-list-title]]:opacity-50"
          : unread ? "[&_[data-slot=item-list-title]]:text-contrast [&_[data-slot=item-list-title]]:font-semibold"
          : "",
        ].filter(Boolean).join(" ")}
        title={
          <>
            {titleOf(discussion) || "New discussion"}
            {discussion.confidential && <i className="ph-bold ph-lock-simple text-[10px] text-text-muted ml-1.5 opacity-60" />}
          </>
        }
        subtitle={<span className="inline-flex items-baseline gap-1.5"><span>{formatRelative(discussion.lastActivity)}</span>{discussion.ownedBy && <span>· {formatOwningApp(discussion.ownedBy.app)}</span>}</span>}
        trailing={
          discussion.status !== "archived" ? (
            <div className="flex items-center gap-1.5">
              {unread && (
                <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
              )}
              <RowActionsMenu label="Conversation actions">
                {discussion.ownedBy ? (
                  <DiscussionActionItems
                    showShortcuts={discussion.id === activeDiscussionId}
                    onInfo={() => onRowAction(discussion.id, "info")}
                    onArtifacts={() => onRowAction(discussion.id, "artifacts")}
                    onFloat={onFloat ? () => onFloat(discussion.id) : undefined}
                  />
                ) : (
                <DiscussionActionItems
                  showShortcuts={discussion.id === activeDiscussionId}
                  onVoice={() => onRowAction(discussion.id, "voice")}
                  voiceDisabled={discussion.status === "stopped"}
                  onInfo={() => onRowAction(discussion.id, "info")}
                  onArtifacts={() => onRowAction(discussion.id, "artifacts")}
                  onRename={() => onRowAction(discussion.id, "rename")}
                  onUpdateTitle={() => onUpdateTitle(discussion.id)}
                  onShare={!discussion.confidential ? () => onRowAction(discussion.id, "share") : undefined}
                  onFloat={onFloat ? () => onFloat(discussion.id) : undefined}
                  onStop={onStop && alive && discussion.sessionId ? () => onStop(discussion.id) : undefined}
                  onClose={() => onArchive(discussion.id)}
                />
                )}
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
  }, [activeDiscussionId, onSelect, onArchive, onDismiss, onRowAction, onUpdateTitle, onFloat, onStop, getAgent, multiAgent, delegationActivity, backgroundTasks, titleOf])

  const renderArchivedItem = useCallback((discussion: DiscussionInfo) => {
    const item = discussion as ArchiveDiscussionItem
    const agent = multiAgent && getAgent ? getAgent(discussion.agentId) : undefined
    return (
      <div
        data-discussion-id={discussion.id}
        data-discussion-type={discussion.type}
        data-discussion-status={discussion.status}
        data-discussion-selected={discussion.id === activeDiscussionId || undefined}
      >
        <ItemListRow
          selected={discussion.id === activeDiscussionId}
          onClick={() => onSelect(discussion.id)}
          icon={agent ? (
            <img
              src={agent.avatarUrl}
              alt={agent.name}
              className="h-full w-full rounded-lg object-cover opacity-70"
              onError={(event) => { event.currentTarget.style.display = "none" }}
            />
          ) : <i aria-hidden="true" className="ph-bold ph-archive text-text-muted" />}
          title={
            <>
              {discussion.type === "live" && <i className="ph-bold ph-broadcast mr-1.5 text-[10px] text-status-live" />}
              {titleOf(discussion) || "Untitled discussion"}
              {discussion.confidential && <i className="ph-bold ph-lock-simple ml-1.5 text-[10px] text-text-muted opacity-60" />}
            </>
          }
          subtitle={item.searchSnippet
            ? <span className="block truncate">{item.searchSnippet}</span>
            : formatRelative(discussion.lastActivity)}
        />
      </div>
    )
  }, [activeDiscussionId, getAgent, multiAgent, onSelect, titleOf])

  if (archiveMode) {
    return (
      <div className="flex h-full flex-col overflow-hidden" data-archive-mode="true">
        <div className="shrink-0 border-b border-overlay-6 p-3">
          <label className="relative block">
            <span className="sr-only">Search archived discussions</span>
            <i aria-hidden="true" className="ph-bold ph-magnifying-glass absolute left-2.5 top-1/2 -translate-y-1/2 text-xs text-text-muted" />
            <input
              type="search"
              value={archiveQuery}
              onChange={event => onArchiveQueryChange?.(event.target.value)}
              onKeyDown={event => {
                if (event.key !== "Escape") return
                event.preventDefault()
                event.stopPropagation()
                if (resolveArchiveSearchEscape(archiveQuery) === "clear")
                  onArchiveQueryChange?.("")
                else
                  onExitArchiveMode?.()
              }}
              placeholder="Search archives…"
              className="h-8 w-full rounded-md border border-overlay-10 bg-overlay-4 pl-8 pr-3 text-xs text-contrast outline-none placeholder:text-text-muted focus:border-accent-a40"
            />
          </label>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto">
          {discussions.length === 0 && loadState === "loading" ? (
            <p data-archive-list-state="loading" className="p-4 text-center text-sm text-text-muted">
              <i aria-hidden="true" className="ph-bold ph-spinner mr-1.5 animate-spin" />
              Loading archived discussions…
            </p>
          ) : discussions.length === 0 && loadState === "failed" ? (
            <div data-archive-list-state="failed" className="p-4 text-center text-sm text-text-muted">
              <p className="mb-3">Couldn&apos;t load archived discussions</p>
              <button onClick={onRetryLoad} className="rounded bg-overlay-10 px-3 py-1.5 text-xs text-contrast hover:bg-overlay-15">
                <i aria-hidden="true" className="ph-bold ph-arrow-clockwise mr-1.5" />Retry
              </button>
            </div>
          ) : (
            <>
              <ItemList
                items={discussions}
                keyFn={discussion => discussion.id}
                emptyMessage={archiveQuery.trim() ? "No archived matches" : "No archived discussions"}
                renderItem={renderArchivedItem}
              />
              {archiveHasMore && (
                <div className="p-3 text-center">
                  <button
                    type="button"
                    onClick={onLoadMoreArchived}
                    disabled={loadState === "loading"}
                    className="rounded-md bg-overlay-8 px-3 py-1.5 text-xs text-text-muted hover:bg-overlay-12 hover:text-contrast disabled:opacity-50"
                  >
                    {loadState === "loading" ? "Loading…" : "Load more"}
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    )
  }

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
