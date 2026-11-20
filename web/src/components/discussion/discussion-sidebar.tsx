import { memo, useCallback, useId, useMemo, useRef } from "react"
import type { ComponentProps } from "react"
import { ItemList, ItemListRow, RowActionsMenu, ScrollArea, useContextTarget, type ContextMenuEntry } from "@redbamboo/ui"
import { MorphSpinner } from "@redbamboo/chat"
import type { BackgroundTaskInfo } from "@redbamboo/chat"
import type { DelegationActivitySnapshot, DiscussionDelegationActivity, DiscussionInfo, AgentInfo } from "../../lib/types"
import type { DiscussionListLoadState } from "../../lib/discussion-list-store"
import { resolveArchiveSearchEscape, type ArchiveDiscussionItem } from "../../lib/archive-discussion-store"

import { resolveLiveSidebarSelection } from "../../lib/live-heartbeat"
import { isDiscussionUnread } from "../../lib/discussion-unread"
import { discussionActivityIndicator } from "../../lib/delegation-activity"
import { discussionActionEntries } from "./discussion-actions"
import { LiveQuotaBorder } from "./live-quota-ring"
import { DiscussionThumbnail } from "./discussion-thumbnail"
import { discussionLabel, discussionSections, groupingIcon, type DiscussionGroupingView } from "../../lib/discussion-grouping"
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
function DiscussionStatusIcon({ discussion, activity, backgroundTasks, awaitingAnswer }: { discussion: DiscussionInfo; activity?: DiscussionDelegationActivity; backgroundTasks?: readonly BackgroundTaskInfo[]; awaitingAnswer?: boolean }) {
  // Parked on a question or an approval: waiting on the person, whatever else runs.
  if (awaitingAnswer && discussion.status !== "archived" && discussion.status !== "archiving") return (
    <span data-slot="discussion-activity" data-activity-source="awaiting-answer" role="img" aria-label="Waiting for your answer" title="Waiting for your answer" className="inline-flex shrink-0">
      <MorphSpinner color="var(--color-amber-300, #fcd34d)" paused />
    </span>
  )
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

function DiscussionUnreadDot() {
  return <span aria-label="Unread" className="w-2 h-2 rounded-full bg-accent-teal shrink-0" />
}

function CollapsedGroupStatus({ discussions, delegationActivity, backgroundTasks, awaitingAnswer }: {
  discussions: readonly DiscussionInfo[]
  delegationActivity: DelegationActivitySnapshot
  backgroundTasks: Props["backgroundTasks"]
  awaitingAnswer: NonNullable<Props["awaitingAnswer"]>
}) {
  const open = discussions.filter(discussion => discussion.status !== "archived" && discussion.status !== "archiving")
  const waiting = open.find(discussion => awaitingAnswer[discussion.id])
  const unread = !waiting && open.some(isDiscussionUnread)
  const activity = waiting || (!unread && open.find(discussion => discussion.status === "thinking" ||
    hasBackgroundWork(discussion, delegationActivity[discussion.id], backgroundTasks[discussion.id])))
  if (unread) return <DiscussionUnreadDot />
  if (!activity) return null

  return (
    <span className="inline-flex shrink-0">
      <DiscussionStatusIcon discussion={activity} activity={delegationActivity[activity.id]} backgroundTasks={backgroundTasks[activity.id]} awaitingAnswer={awaitingAnswer[activity.id]} />
    </span>
  )
}

/** A sidebar row whose right-click and long-press menu is its ⋮ menu's entries. */
function DiscussionRowTarget({ entries, ...props }: { entries: ContextMenuEntry[] } & ComponentProps<"div">) {
  const ref = useRef<HTMLDivElement>(null)
  useContextTarget(ref, () => entries)
  return <div ref={ref} {...props} />
}

interface Props {
  discussions: DiscussionInfo[]
  loadState: DiscussionListLoadState
  onRetryLoad: () => void
  delegationActivity: DelegationActivitySnapshot
  /** Live background tasks (subagents, shells) per discussion id. */
  backgroundTasks: Record<string, BackgroundTaskInfo[]>
  awaitingAnswer?: Record<string, boolean>
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
  /** Show each row's Agent avatar, with the status as a corner badge. */
  showAvatars?: boolean
  grouping?: DiscussionGroupingView | null
  groupingEnabled?: boolean
  groupingLoading?: boolean
  groupingUnavailable?: boolean
  onRetryGrouping?: () => void
  collapsedGroups: readonly (string | null)[]
  onToggleGroup: (group: string | null) => void
  archiveMode?: boolean
  archiveQuery?: string
  archiveHasMore?: boolean
  onArchiveQueryChange?: (query: string) => void
  onExitArchiveMode?: () => void
  onLoadMoreArchived?: () => void
}

export const DiscussionSidebar = memo(function DiscussionSidebar({ discussions, loadState, onRetryLoad, delegationActivity, backgroundTasks, awaitingAnswer = {}, activeDiscussionId, onSelect, onArchive, onRotate, onDismiss, onRowAction, onUpdateTitle, onFloat, onStop, getAgent, showAvatars = true, grouping = null, groupingEnabled = false, groupingLoading = false, groupingUnavailable = false, onRetryGrouping, collapsedGroups, onToggleGroup, archiveMode = false, archiveQuery = "", archiveHasMore = false, onArchiveQueryChange, onExitArchiveMode, onLoadMoreArchived }: Props) {
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
  const sections = useMemo(() => discussionSections(chat, grouping), [chat, grouping])
  const hasNamedSections = sections.some(section => section.name !== null)
  const groupListId = useId()

  const renderLiveItem = useCallback((discussion: DiscussionInfo) => {
    const unread = isDiscussionUnread(discussion)
    const agent = showAvatars ? getAgent?.(discussion.agentId) : undefined
    const entries = discussionActionEntries({
      showShortcuts: discussion.id === activeDiscussionId,
      onVoice: () => onRowAction(discussion.id, "voice"),
      voiceDisabled: discussion.status === "stopped",
      onInfo: () => onRowAction(discussion.id, "info"),
      onArtifacts: () => onRowAction(discussion.id, "artifacts"),
      onShare: !discussion.confidential ? () => onRowAction(discussion.id, "share") : undefined,
      onFloat: onFloat ? () => onFloat(discussion.id) : undefined,
      onReset: () => onRotate(discussion.id),
    })
    return (
      <DiscussionRowTarget
        entries={entries}
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
          <LiveQuotaBorder discussion={discussion}>
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
                    <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} awaitingAnswer={awaitingAnswer[discussion.id]} />
                  </div>
                </>
              ) : (
                <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} awaitingAnswer={awaitingAnswer[discussion.id]} />
              )}
            </div>
          </LiveQuotaBorder>
        }
        className={[
          "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible",
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
              <DiscussionUnreadDot />
            )}
            <RowActionsMenu label="Live actions" entries={entries} />
          </div>
        }
      />
      </DiscussionRowTarget>
    )
  }, [sidebarSelectionId, activeDiscussionId, onSelect, onRotate, onRowAction, onFloat, getAgent, showAvatars, delegationActivity, backgroundTasks, awaitingAnswer])

  const renderChatItem = useCallback((discussion: DiscussionInfo) => {
    const label = discussionLabel(discussion, grouping)
    const visualCue = !!label && showAvatars
    const alive = discussion.status !== "archived" && discussion.status !== "stopped"
    const unread = alive && isDiscussionUnread(discussion)
    const agent = showAvatars ? getAgent?.(discussion.agentId) : undefined
    const entries: ContextMenuEntry[] = discussion.status === "archived"
      ? [{ id: "remove", label: "Remove from list", icon: "ph-bold ph-x", run: () => onDismiss(discussion.id) }]
      : discussion.ownedBy
        ? discussionActionEntries({
            showShortcuts: discussion.id === activeDiscussionId,
            onInfo: () => onRowAction(discussion.id, "info"),
            onArtifacts: () => onRowAction(discussion.id, "artifacts"),
            onFloat: onFloat ? () => onFloat(discussion.id) : undefined,
          })
        : discussionActionEntries({
            showShortcuts: discussion.id === activeDiscussionId,
            onVoice: () => onRowAction(discussion.id, "voice"),
            voiceDisabled: discussion.status === "stopped",
            onInfo: () => onRowAction(discussion.id, "info"),
            onArtifacts: () => onRowAction(discussion.id, "artifacts"),
            onRename: () => onRowAction(discussion.id, "rename"),
            onUpdateTitle: () => onUpdateTitle(discussion.id),
            onShare: !discussion.confidential ? () => onRowAction(discussion.id, "share") : undefined,
            onFloat: onFloat ? () => onFloat(discussion.id) : undefined,
            onStop: onStop && alive && discussion.sessionId ? () => onStop(discussion.id) : undefined,
            onClose: () => onArchive(discussion.id),
          })
    return (
      <DiscussionRowTarget
        entries={entries}
        title={titleOf(discussion) || "New discussion"}
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
            {agent || visualCue ? (
              <>
                {visualCue ? <DiscussionThumbnail id={discussion.id} icon={groupingIcon(label?.icon)} target={label?.thumbnailTarget} /> : agent && <img
                  src={agent.avatarUrl}
                  alt={agent.name}
                  className="absolute inset-0 w-full h-full rounded-lg object-cover"
                  onError={(e) => { e.currentTarget.style.display = "none" }}
                />}
                <div className="absolute -bottom-1 right-0 origin-bottom-right scale-75">
                  <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} awaitingAnswer={awaitingAnswer[discussion.id]} />
                </div>
              </>
            ) : (
              <DiscussionStatusIcon discussion={discussion} activity={delegationActivity[discussion.id]} backgroundTasks={backgroundTasks[discussion.id]} awaitingAnswer={awaitingAnswer[discussion.id]} />
            )}
          </div>
        }
        className={[
          visualCue || agent || hasBackgroundWork(discussion, delegationActivity[discussion.id], backgroundTasks[discussion.id]) ? "[&_[data-slot=item-list-icon]]:relative [&_[data-slot=item-list-icon]]:overflow-visible" : "",
          !alive ? "[&_[data-slot=item-list-title]]:opacity-50"
          : unread ? "[&_[data-slot=item-list-title]]:text-contrast [&_[data-slot=item-list-title]]:font-semibold"
          : "",
        ].filter(Boolean).join(" ")}
        title={
          <>
            {label?.label || titleOf(discussion) || "New discussion"}
            {discussion.confidential && <i className="ph-bold ph-lock-simple text-[10px] text-text-muted ml-1.5 opacity-60" />}
          </>
        }
        subtitle={<span className="inline-flex items-baseline gap-1.5"><span>{formatRelative(discussion.lastActivity)}</span>{visualCue && agent && <span>· {agent.name}</span>}{discussion.ownedBy && <span>· {formatOwningApp(discussion.ownedBy.app)}</span>}</span>}
        trailing={
          discussion.status !== "archived" ? (
            <div className="flex items-center gap-1.5">
              {unread && (
                <DiscussionUnreadDot />
              )}
              <RowActionsMenu label="Conversation actions" entries={entries} />
            </div>
          ) : (
            <RowActionsMenu label="Conversation actions" entries={entries} />
          )
        }
      />
      </DiscussionRowTarget>
    )
  }, [activeDiscussionId, onSelect, onArchive, onDismiss, onRowAction, onUpdateTitle, onFloat, onStop, getAgent, showAvatars, delegationActivity, backgroundTasks, awaitingAnswer, titleOf, grouping])

  const renderArchivedItem = useCallback((discussion: DiscussionInfo) => {
    const item = discussion as ArchiveDiscussionItem
    const agent = showAvatars ? getAgent?.(discussion.agentId) : undefined
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
  }, [activeDiscussionId, getAgent, showAvatars, onSelect, titleOf])

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
          groupingEnabled ? (
            <ScrollArea data-slot="item-list" className="h-full" data-discussion-grouping="true">
              <div className={hasNamedSections ? "flex flex-col gap-2 p-2" : "flex flex-col"}>
                {groupingLoading && !grouping?.assignments.length && <p role="status" className="px-4 py-2 text-[11px] text-text-muted">Organizing discussions…</p>}
                {groupingUnavailable && <div className="flex items-center justify-between gap-2 px-4 py-2 text-[11px] text-text-muted"><span>Project grouping is unavailable</span><button type="button" className="hover:text-contrast" onClick={onRetryGrouping}>Retry</button></div>}
                {sections.map((section, index) => {
                  const heading = section.name ?? (hasNamedSections ? "Ungrouped" : null)
                  const collapsed = heading !== null && collapsedGroups.includes(section.name)
                  const containsSelection = collapsed && section.items.some(discussion => discussion.id === activeDiscussionId)
                  const listId = `${groupListId}-${index}`
                  return (
                    <section
                      className="flex flex-col gap-2"
                      key={JSON.stringify(section.name)}
                      aria-label={heading ?? "Other discussions"}
                      data-discussion-group={section.name ?? "ungrouped"}
                    >
                      {heading && <h3>
                        <button
                          type="button"
                          aria-expanded={!collapsed}
                          aria-controls={listId}
                          onClick={() => onToggleGroup(section.name)}
                          className={`flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-xs font-medium transition-colors hover:bg-overlay-6 hover:text-contrast focus-visible:outline focus-visible:-outline-offset-2 focus-visible:outline-accent-teal ${containsSelection ? "text-contrast" : "text-text-muted"}`}
                        >
                          <i aria-hidden="true" className={`ph-bold ph-caret-${collapsed ? "right" : "down"} shrink-0 text-[10px]`} />
                          {section.name && <span className="relative h-6 w-6 shrink-0" data-group-visual={section.name}>
                            {section.thumbnailDiscussionId && section.thumbnailTarget && showAvatars
                              ? <DiscussionThumbnail id={section.thumbnailDiscussionId} icon={section.icon} target={section.thumbnailTarget} />
                              : <i aria-hidden="true" className={`ph ph-${groupingIcon(section.icon)} absolute inset-0 flex items-center justify-center text-sm`} />}
                          </span>}
                          <span className="min-w-0 flex-1 truncate" title={heading}>{heading}</span>
                          {collapsed && (
                            <>
                              <span className="shrink-0 tabular-nums">
                                {section.items.length}
                                <span className="sr-only"> {section.items.length === 1 ? "discussion" : "discussions"}</span>
                              </span>
                              <CollapsedGroupStatus discussions={section.items} delegationActivity={delegationActivity} backgroundTasks={backgroundTasks} awaitingAnswer={awaitingAnswer} />
                            </>
                          )}
                        </button>
                      </h3>}
                      <div
                        id={listId}
                        className={collapsed ? "hidden" : heading ? "flex flex-col gap-1" : "flex flex-col in-data-[cozy]:gap-0.5"}
                        hidden={collapsed}
                      >
                        {!collapsed && section.items.map(discussion => <div key={discussion.id}>{renderChatItem(discussion)}</div>)}
                      </div>
                    </section>
                  )
                })}
                {chat.length === 0 && <p className="p-4 text-center text-sm text-text-muted">No discussions yet</p>}
              </div>
            </ScrollArea>
          ) : <ItemList
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
