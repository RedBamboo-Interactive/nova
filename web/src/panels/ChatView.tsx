import { useState, useCallback, useDeferredValue, useEffect, useLayoutEffect, useMemo, useRef, type ButtonHTMLAttributes, type MouseEvent as ReactMouseEvent, type Ref } from "react"
import { useParams, useNavigate, useLocation } from "react-router-dom"
import { JournalView } from "../components/journal/journal-view"
import { JOURNAL_ROUTE, isJournalPath, journalPath } from "../lib/journal"
import { DropdownMenu, DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger, MasterDetailLayout, PanelHeader, Popover, PopoverContent, PopoverHeader, PopoverTitle, PopoverTrigger, Tabs, TabsList, TabsTrigger, normalizeIcon, useToast, useUiEnvironment } from "@redbamboo/ui"
import { ChatPanel, ConversationActionsMenu, EditableTitle, MESSAGE_PARAM, PendingContextAttachment, SessionInfoButton, ShareDialog, discussionShareSource, fetchTranscriptPayload, messageDeepLinkStep, useConversationShortcuts, useGoToMessage, useMaintenanceStatus, useShare, useTitleUpdate, withoutMessageParam, useComposerFocusSettings, usePushToTalkSettings, useChatJobs, type AttachmentTransport, type ChatJob, type ChatInputPart, type ChatQueueSnapshot, type ChatQueueTransport, type ChatQueuedItem, type ImageAttachment, type OutgoingMessageDraft, type SendOptions, type MessageBlock, type ParsedEvent, type RenderEventDetails, type PermissionDecisionPayload, type ProviderUsageSnapshot, type AccessLevel, type QuestionAnswerPayload, type TranscriptPayloadLoader, type TranscriptPayloadRef, type UploadedAttachment, ChatBlockRenderersProvider, eventInputMessageUid, parseEventPart, type EventQueueStatus, type ResolveEventQueueStatus } from "@redbamboo/chat"
import { LiveReportBlock, LiveReportHostProvider, type LiveReportHost } from "../components/live-report/live-report"
import { LIVE_REPORT_LANGUAGE } from "../lib/live-report"
import { captureVisibleAppContext, useBreadcrumbLabel, formatContextMessage, getEntityHref, PluginExtensionSlot, runUiSurfaceAction, useCommand, useUiSurface, VisibleAppContextCaptureError, type UiSurfaceActionResult, type VisibleAppContext } from "@redbamboo/utility"
import { DiscussionSidebar, type DiscussionRowAction } from "../components/discussion/discussion-sidebar"
import { DiscussionThumbnail, DiscussionThumbnailCacheContext, useDiscussionThumbnailCache } from "../components/discussion/discussion-thumbnail"
import { discussionGroupSortOptions, discussionLabel, discussionSections, groupedDiscussionSortOptions, groupingIcon, moveDiscussionGroup, type DiscussionGroupSortMode, type GroupedDiscussionSortMode } from "../lib/discussion-grouping"
import { isMobileClient } from "../components/floating-nova-support"
import { DISCUSSION_SHORTCUTS, discussionActionEntries } from "../components/discussion/discussion-actions"
import { ArtifactsSurface } from "../components/discussion/artifacts-surface"
import { AgentPicker } from "../components/agent-picker"
import { TransitioningAgentAvatar } from "../components/transitioning-agent-avatar"
import { NovaStatusLine } from "../components/nova-status-line"
import { DelegationsInfo, delegationActivityGroup } from "../components/delegation-activity"
import { withWaitingActivity } from "../lib/waiting-activity"
import { permissionNoticeText } from "../lib/permission-notices"
import { WorktreeChips } from "../components/discussion/worktree-chips"
import { DiscussionBehaviourList, OrchestrationStatus, type BehaviourItem } from "../components/discussion/discussion-behaviour"
import { BEHAVIOUR_ICONS, agentsWithHeartbeat, effectiveBehaviours, hasIntrinsicBehaviours } from "../lib/discussion-behaviours"
import { AttachSkillDialog, SkillChips } from "../components/discussion/skill-chips"
import { ReactionPills, AddReactionButton, useReactionEmoji } from "../components/discussion/reactions"
import { reactionMenuEntries } from "../components/discussion/reaction-actions"
import { createNovaSpeechBackend } from "../lib/speech"
import { useLocalSettings } from "../hooks/use-local-settings"
import { JOB_MARKER_KEY, friezeMarkerScope, friezeMarkerVisibility, normalizeFriezeDensity, normalizeHiddenFriezeMarkers } from "../lib/frieze-marker-settings"
import { computeJobHref, createDiscussionJobsTransport, jobFrameConcernsDiscussion, stopDiscussionJob } from "../lib/discussion-jobs"
import { useAgents } from "../hooks/use-agents"
import { versionedAvatarSrc } from "../lib/agent-directory-sync"
import { recordingUsageLoader, resolveLiveProvider, usageKey } from "../lib/provider-usage-store"
import { LiveQuotaContext, providerUsageStore, type LiveQuotaContextValue } from "../components/discussion/live-quota-ring"
import { useReactions } from "../hooks/use-reactions"
import { useDisc, useNovaPendingContext, useSidebarGrouping } from "../App"
import { useSessionStats } from "../hooks/use-session-stats"
import { useSessionInfoSources } from "../hooks/use-session-info-sources"
import { DiscussionRunOnSetting, runOnAvailable } from "../components/run-on"
import { useFullAccessEnabled } from "../hooks/use-full-access-enabled"
import { FULL_ACCESS_SETTINGS_HREF, changeAccessLevel, openSettingsFromLink } from "../lib/session-access"
import { getSettings, setSettings } from "../lib/settings-store"
import { api, novaExecution } from "../lib/api"
import { findCurrentLive, findLiveHeartbeatPair } from "../lib/live-heartbeat"
import { getAdjacentSidebarDiscussion, getSidebarDiscussionOrder } from "../lib/discussion-navigation"
import { createDiscussionTitleResolver } from "../lib/discussion-display-title"
import {
  FLOATING_NOVA_NAVIGATION_EVENT,
  FLOATING_NOVA_SHORTCUT_LIST,
  getFloatingNovaNavigationAction,
  type FloatingNovaNavigationAction,
} from "../lib/floating-navigation"
import {
  FLOATING_NOVA_CAPTURE_CONTEXT_EVENT,
  type FloatingNovaCaptureContextRequest,
} from "../lib/floating-context"
import { applyPendingVisibleContext } from "../lib/pending-visible-context-store"
import { fadeOutgoingTranscript } from "../lib/discussion-transcript-fade"
import { isDiscussionSelectionCurrent, resolveRequestedDiscussionId } from "../lib/discussion-view-selection"
import { NOVA_DRAFT_STORAGE_KEY, opensChat } from "../lib/new-discussion-draft"
import { captureMonitorVisualSource, listMonitorVisualSources, monitorCaptureToContext, type MonitorVisualSource } from "../lib/visual-capture"
import { delegationEventPath } from "../lib/delegation-session-link"
import { AGENT_NOTE_KEY } from "../lib/agent-note"
import { AgentNoteExchange } from "../components/discussion/agent-note-exchange"
import type { DiscussionInfo } from "../lib/types"
import { DelegationExchange } from "../components/discussion/delegation-exchange"
import { describeAgentNote, type NoteOriginDiscussion } from "../lib/agent-note-presentation"
import { HELD_EVENT_LABEL } from "../lib/held-events"
import { formatOwningApp } from "../lib/discussion-owner"
import { useVoiceSession } from "../hooks/use-voice-session"
import { VoiceSessionPanel } from "../components/voice-session-panel"

const NO_MESSAGES: MessageBlock[] = []
const COMPOSER_INPUT = '[data-slot="composer"] textarea'

function keepComposerFocus(event: ReactMouseEvent<HTMLDivElement>) {
  const row = (event.target as Element).closest?.('[data-discussion-id] [data-slot="item-list-row"]')
  if (event.button !== 0 || !row) return

  const focused = event.currentTarget.ownerDocument.activeElement
  if (focused?.matches(`[data-slot="nova-discussion-pane"] ${COMPOSER_INPUT}`)) {
    event.preventDefault()
  }
}

const speechBackend = createNovaSpeechBackend()
const loadNovaMaintenanceStatus = () => api.get<unknown>("/api/apps/nova/maintenance")

interface QualityTierInfo { slug: string; label: string; color?: string; icon?: string }
interface ProviderInfo {
  slug: string
  name: string
  backend: string
  icon?: string
  iconSvgPath?: string
  color?: string
}

interface DiscussionShortcutsProps {
  discussionId: string
  voiceAvailable: boolean
  canManage: boolean
  canShare: boolean
  isLive: boolean
  openVoice: () => void
  openSessionInfo: () => void
  toggleArtifacts: () => void
  startRename: () => void
  updateTitle: (id: string) => Promise<void> | void
  share: () => void
  rotate: (id: string) => Promise<void> | void
}

// Desktop shortcuts for the open discussion; the menus show the same keys.
// Mounted only for the docked chat: the floating window is a shell service that
// lives outside the shell's CommandProvider, where useCommand throws.
function DiscussionShortcuts({
  discussionId,
  voiceAvailable,
  canManage,
  canShare,
  isLive,
  openVoice,
  openSessionInfo,
  toggleArtifacts,
  startRename,
  updateTitle,
  share,
  rotate,
}: DiscussionShortcutsProps) {
  useCommand("nova-discussion-voice", {
    label: "Open Voice",
    description: "Open voice call controls for the current discussion",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.voice,
    enabled: voiceAvailable,
    action: openVoice,
  })

  // Close stays app-wide in the shell (nova-close-discussion), so it is not registered here.
  useConversationShortcuts({
    idPrefix: "nova-discussion",
    group: "Discussions",
    noun: "Discussion",
    info: { action: openSessionInfo, description: "Show the session, model and delegations of the open discussion" },
    artifacts: { action: toggleArtifacts },
    rename: { action: startRename, enabled: canManage },
    updateTitle: { action: () => void updateTitle(discussionId), enabled: canManage },
    share: { action: share, enabled: canShare },
  })

  useCommand("nova-reset-live-timeline", {
    label: "Reset Live Timeline",
    description: "Rotate Live onto a fresh timeline",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.reset,
    keywords: ["rotate", "reset", "live"],
    enabled: isLive,
    action: () => void rotate(discussionId),
  })

  return null
}

function ChatHeaderAction({
  icon,
  label,
  buttonRef,
  ...props
}: {
  icon: string
  label: string
  buttonRef?: Ref<HTMLButtonElement>
} & ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      ref={buttonRef}
      type="button"
      {...props}
      aria-label={props["aria-label"] ?? label}
      data-slot="chat-header-action"
      className="inline-flex h-7 shrink-0 items-center justify-center gap-1.5 rounded-md px-2 text-xs font-medium text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 disabled:pointer-events-none disabled:opacity-50"
    >
      <i aria-hidden="true" className={`${icon} text-sm`} />
      <span>{label}</span>
    </button>
  )
}

function useAvatarStyle() {
  const [opacity, setOpacity] = useState(0.9)
  useEffect(() => {
    const update = () => {
      setOpacity(document.documentElement.dataset.contrast === "low" ? 0.7 : 0.9)
    }
    update()
    const observer = new MutationObserver(update)
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-contrast"] })
    return () => observer.disconnect()
  }, [])
  return { opacity }
}

function resolveImageSrc(src: string): string | undefined {
  if (/^[A-Za-z]:[\\\/]/.test(src))
    return `/api/apps/nova/file?path=${encodeURIComponent(src)}`
  return src
}

function navigateCodeRed(path: string) {
  // CodeRed is a plugin on this origin now: the kernel bridges codered.navigate
  // onto /ws and the shell routes to /apps/codered client-side.
  novaExecution.fetch(`/api/apps/codered/navigate?path=${encodeURIComponent(path)}`, {
    method: "POST",
    credentials: "include",
  }).catch(() => {})
}

// Three tabs and the menu share the phone's row beside the avatar, so each gives up some air there.
const LIVE_TAB_CLASS = "after:rounded-full after:bg-primary group-data-[orientation=horizontal]/tabs:after:inset-x-1 group-data-[orientation=horizontal]/tabs:after:bottom-[-2px] max-md:gap-1 max-md:px-1 max-md:text-[13px]"

export interface ChatViewProps {
  presentation?: "standard" | "floating"
  selectedDiscussionId?: string | null
  onSelectDiscussion?: (id: string) => void
  onNewDiscussion?: () => void
  onDock?: () => void
}

/** Fenced blocks Nova renders itself in its messages: one stable map, so the chat never re-creates its components. */
const HELD_EVENT_STATUS: EventQueueStatus = { label: HELD_EVENT_LABEL, failed: false, pending: true, actions: null }
const NOVA_BLOCKS = { [LIVE_REPORT_LANGUAGE]: LiveReportBlock }

export function ChatView({
  presentation = "standard",
  selectedDiscussionId = null,
  onSelectDiscussion,
  onNewDiscussion,
  onDock,
}: ChatViewProps = {}) {
  const { discussionId: urlDiscussionId, date: journalDate } = useParams()
  const navigate = useNavigate()
  const disc = useDisc()
  const environment = useUiEnvironment()
  const floating = presentation === "floating"
  const mobileClient = isMobileClient(environment.window.navigator)
  const [pendingDiscussionId, setPendingDiscussionId] = useState<string | null>(null)
  const discussionPaneRef = useRef<HTMLDivElement>(null)
  const transcriptFadeRef = useRef<HTMLDivElement>(null)
  const restoreComposerFocusRef = useRef<string | null>(null)
  const location = useLocation()
  const [mobileTab, setMobileTab] = useState(() => (!floating && opensChat(location.state) ? 1 : 0))
  const floatingSurface = useUiSurface("nova:floating-chat")

  // Intercept clicks on CodeRed links and navigate via API instead
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      const anchor = (e.target as HTMLElement).closest("a")
      if (!anchor) return
      const href = anchor.getAttribute("href")
      if (!href) return
      try {
        const url = new URL(href, environment.window.location.origin)
        if (url.hostname === "localhost" && url.port === "18801") {
          e.preventDefault()
          navigateCodeRed(url.pathname + url.search)
        }
      } catch {}
    }
    environment.document.addEventListener("click", handler)
    return () => environment.document.removeEventListener("click", handler)
  }, [environment.document, environment.window.location.origin])

  const {
    discussions,
    discussionListLoadState,
    archivedDiscussions,
    archiveMode,
    archiveListLoadState,
    archiveQuery,
    archiveHasMore,
    setArchiveMode,
    setArchiveQuery,
    refreshArchivedDiscussions,
    activeDiscussion,
    activeDiscussionId,
    activeMessages,
    isStreaming,
    supportsSteering,
    supportsRollback,
    rollbackDiscussion,
    backgroundTasks,
    backgroundTasksByDiscussion,
    awaitingAnswerByDiscussion,
    stopBackgroundTask,
    isInterrupting,
    isResumePending,
    isSpawning,
    pendingQuestion,
    questionOutcome,
    permissionApprovals,
    permissionNotices,
    selectDiscussion,
    clearDiscussionSelection,
    setReadAcknowledgementPaused,
    sendMessage,
    sendInput,
    interruptDiscussion,
    stopDiscussion,
    answerQuestion,
    decidePermission,
    archiveDiscussion,
    rotateDiscussion,
    dismissDiscussion,
    renameDiscussion,
    updateDiscussionTitle,
    setConfidential,
    setLiveTimeline,
    setOrchestrated,
    setRunOnNode,
    reopenDiscussion,
    loadEarlierMessages,
    retryHistoryLoad,
    isLoadingMessages,
    isResolvingDiscussion,
    hasEarlierMessages,
    heldEventUids,
    isLoadingEarlier,
    historyLoadFailure,
    earlierHistoryLoadFailure,
    upstreamConnected,
    delegationActivity,
    refreshDelegationActivity,
    syncAndRefresh,
  } = disc

  const { agents, getAgent, defaultAgentId, refreshAgents, avatarRevision } = useAgents()

  // The Journal is a route beside a discussion, and the chat under it stays on the Agent's Live.
  // Floating has no route of its own to stand on, so it keeps its two tabs.
  const journalOpen = !floating && isJournalPath(location.pathname)
  const journalAgent = journalOpen ? new URLSearchParams(location.search).get("agent") : null
  const journalLive = journalOpen ? findCurrentLive(discussions, journalAgent ?? defaultAgentId) : null
  const requestedDiscussionId = floating
    ? selectedDiscussionId
    : journalOpen ? (journalLive?.id ?? null) : (urlDiscussionId ?? null)

  const defaultDiscussionId = getSidebarDiscussionOrder(discussions, null)[0]?.id ?? null
  const synchronizedDiscussionId = resolveRequestedDiscussionId(
    requestedDiscussionId,
    pendingDiscussionId,
    defaultDiscussionId,
  )
  const discussionSelectionPending = !isDiscussionSelectionCurrent(synchronizedDiscussionId, activeDiscussionId)
  // Give the selected row and chat frame a paint before mounting a large cached transcript.
  const renderedDiscussionId = useDeferredValue(synchronizedDiscussionId)
  const historyRenderPending = renderedDiscussionId !== synchronizedDiscussionId

  const maintenance = useMaintenanceStatus({
    load: loadNovaMaintenanceStatus,
    refreshEvent: "nova:maintenance-refresh",
  })
  const voiceSession = useVoiceSession(activeDiscussionId)
  const conversationRollback = useMemo(() => activeDiscussionId
    ? {
        supported: supportsRollback,
        request: (messageUid: string) => rollbackDiscussion(activeDiscussionId, messageUid),
      }
    : undefined, [activeDiscussionId, supportsRollback, rollbackDiscussion])
  const voiceSessionTriggerRef = useRef<HTMLButtonElement>(null)
  const payloadSessionId = activeDiscussion?.sessionId ?? null
  const loadTranscriptPayload = useCallback<TranscriptPayloadLoader>((ref, range, signal) => {
    if (!activeDiscussionId) return Promise.reject(new Error("This discussion is not active"))
    const url = `/api/apps/nova/discussions/${encodeURIComponent(activeDiscussionId)}/messages/${ref.recordId}/output`
    return fetchTranscriptPayload(url, ref, range, signal)
  }, [activeDiscussionId])
  const getTranscriptPayloadDownloadUrl = useCallback((ref: TranscriptPayloadRef) =>
    activeDiscussionId
      ? `/api/apps/nova/discussions/${encodeURIComponent(activeDiscussionId)}/messages/${ref.recordId}/output?download=true`
      : "#",
  [activeDiscussionId])
  const getTranscriptPayloadMediaUrl = useCallback((ref: TranscriptPayloadRef) =>
    activeDiscussionId
      ? `/api/apps/nova/discussions/${encodeURIComponent(activeDiscussionId)}/messages/${ref.recordId}/output`
      : "#",
  [activeDiscussionId])

  // On a phone the Journal lives on the chat side of the layout.
  useLayoutEffect(() => {
    if (journalOpen) setMobileTab(1)
  }, [journalOpen])

  // Before the selection below: opening the Journal must not mark the Live under it as read.
  // Coming back to Live is reading it; leaving this view altogether is not.
  useLayoutEffect(() => {
    if (!floating) setReadAcknowledgementPaused(journalOpen, true)
  }, [floating, journalOpen, setReadAcknowledgementPaused])
  useLayoutEffect(() => () => {
    if (!floating) setReadAcknowledgementPaused(false, false)
  }, [floating, setReadAcknowledgementPaused])

  useLayoutEffect(() => {
    if (pendingDiscussionId && requestedDiscussionId === pendingDiscussionId) {
      setPendingDiscussionId(null)
    }
    if (synchronizedDiscussionId && synchronizedDiscussionId !== activeDiscussionId) {
      selectDiscussion(synchronizedDiscussionId)
      setMobileTab(1)
    } else if (!synchronizedDiscussionId && activeDiscussionId) {
      clearDiscussionSelection()
      setMobileTab(0)
    }
  }, [requestedDiscussionId, pendingDiscussionId, synchronizedDiscussionId, activeDiscussionId, selectDiscussion, clearDiscussionSelection])

  // Keep the existing conversation remount, but transfer input focus before paint.
  useLayoutEffect(() => {
    if (!activeDiscussion || discussionSelectionPending || restoreComposerFocusRef.current !== activeDiscussion.id) return
    restoreComposerFocusRef.current = null
    discussionPaneRef.current?.querySelector<HTMLTextAreaElement>(COMPOSER_INPUT)?.focus({ preventScroll: true })
  }, [activeDiscussion, discussionSelectionPending])

  // Same-day Lives are told apart against every discussion this view has loaded.
  const activeDisplayTitle = useMemo(() => activeDiscussion
    ? createDiscussionTitleResolver([...discussions, ...archivedDiscussions])(activeDiscussion)
    : null, [activeDiscussion, discussions, archivedDiscussions])
  const discussionClosed = activeDiscussion?.status === "archived" || activeDiscussion?.status === "archiving"

  // The route's segment would read "journal"; a dated day has its own crumb under it.
  useBreadcrumbLabel(journalOpen ? JOURNAL_ROUTE : undefined, "Journal")
  useBreadcrumbLabel(journalOpen && journalDate ? journalPath({ day: journalDate }) : undefined, "Journal")
  const multiAgent = agents.length > 1
  const settings = useLocalSettings()
  const pushToTalk = usePushToTalkSettings()
  const { agentFilter, collapsedDiscussionGroups, discussionGroupSort, groupedDiscussionSort } = settings
  const { grouping, groupSort } = useSidebarGrouping()

  const toggleDiscussionGroup = useCallback((group: string | null) => {
    const collapsed = getSettings().collapsedDiscussionGroups
    setSettings({
      collapsedDiscussionGroups: collapsed.includes(group)
        ? collapsed.filter(name => name !== group)
        : [...collapsed, group],
    })
  }, [])

  const thumbnails = useDiscussionThumbnailCache(discussions)
  const reorderDiscussionGroups = useCallback((source: string, target: string, visibleNames: readonly string[]) => {
    const current = getSettings()
    if (!current.groupDiscussions || current.discussionGroupSort !== "manual") return
    const sections = discussionSections(discussions, grouping.view, { mode: "manual", manualOrder: current.discussionGroupOrder })
    const order = new Set(current.discussionGroupOrder)
    for (const section of sections) {
      if (section.name !== null) order.add(section.name)
    }
    const next = moveDiscussionGroup([...order], visibleNames, source, target)
    if (next) setSettings({ discussionGroupOrder: next })
  }, [discussions, grouping.view])
  const filteredDiscussions = getSidebarDiscussionOrder(discussions, agentFilter)
  // From the unfiltered list: the sidebar order drops Heartbeat discussions.
  const heartbeatAgents = useMemo(() => agentsWithHeartbeat(discussions), [discussions])
  const filteredArchivedDiscussions = getSidebarDiscussionOrder(archivedDiscussions, agentFilter)

  useEffect(() => {
    if (!archiveMode) return
    const timer = environment.window.setTimeout(() => {
      void refreshArchivedDiscussions(archiveQuery, agentFilter).catch(() => {})
    }, archiveQuery.trim() ? 300 : 0)
    return () => environment.window.clearTimeout(timer)
  }, [agentFilter, archiveMode, archiveQuery, environment.window, refreshArchivedDiscussions])

  const pendingContext = useNovaPendingContext()
  const { toast } = useToast()
  const activeAgent = activeDiscussion ? getAgent(activeDiscussion.agentId) : undefined
  const unresolvedAgentRefresh = useRef<string | null>(null)
  const [fileRepositories, setFileRepositories] = useState<Array<{ path: string }>>([])

  useEffect(() => {
    const agentId = activeDiscussion?.agentId
    if (!agentId || activeAgent) {
      unresolvedAgentRefresh.current = null
      return
    }
    if (agents.length === 0 || unresolvedAgentRefresh.current === agentId) return
    unresolvedAgentRefresh.current = agentId
    refreshAgents()
  }, [activeAgent, activeDiscussion?.agentId, agents.length, refreshAgents])

  useEffect(() => {
    api.get<{ items: Array<{ path: string }> }>("/api/apps/codered/repositories")
      .then((catalog) => setFileRepositories(catalog.items))
      .catch(() => setFileRepositories([]))
  }, [])

  const openLocalFile = useCallback(async (filePath: string, opts?: { line?: number }) => {
    const normalizedPath = filePath.replace(/\\/g, "/")
    const lowerPath = normalizedPath.toLowerCase()
    const repository = fileRepositories
      .map((item) => item.path.replace(/\\/g, "/").replace(/\/+$/, ""))
      .filter((root) => lowerPath === root.toLowerCase() || lowerPath.startsWith(`${root.toLowerCase()}/`))
      .sort((left, right) => right.length - left.length)[0]

    if (repository) {
      const relativePath = normalizedPath.slice(repository.length).replace(/^\/+/, "")
      const file = relativePath ? `/${encodeURIComponent(relativePath)}` : ""
      const line = opts?.line ? `?line=${opts.line}` : ""
      navigateCodeRed(`/code/${encodeURIComponent(repository)}${file}${line}`)
      return
    }

    try {
      const result = await api.post<{ action: "markdown" | "workspace" | "explorer"; path: string }>(
        "/api/apps/nova/file/open",
        { path: filePath, agentId: activeAgent?.id ?? null },
      )
      if (result.action === "markdown" && activeAgent) {
        setSettings({ agentFilter: activeAgent.id })
        const routePath = result.path.split("/").map(encodeURIComponent).join("/")
        navigate(`/apps/nova/journal/${routePath}`)
      } else if (result.action === "workspace" && activeAgent?.workspaceId) {
        navigate(`/workspace/${activeAgent.workspaceId}`)
      }
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not open file",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    }
  }, [activeAgent, fileRepositories, navigate, toast])

  const resolveFileLink = useCallback((filePath: string, opts?: { line?: number }) => (
    () => { void openLocalFile(filePath, opts) }
  ), [openLocalFile])

  const sessionStatsDiscussion = useMemo(() => activeDiscussion && { ...activeDiscussion, title: activeDisplayTitle },
    [activeDiscussion, activeDisplayTitle])
  const { stats: sessionStats, applyAccessLevel, nodeId: sessionNodeId, projectPath: sessionProjectPath } = useSessionStats(activeDiscussion?.sessionId, isStreaming, sessionStatsDiscussion)
  const loadProviderUsage = useCallback((provider: string, forceRefresh = false) =>
    api.get<ProviderUsageSnapshot>(
      `/ai-session/providers/${encodeURIComponent(provider)}/usage${forceRefresh ? "?refresh=true" : ""}`,
    ), [])
  const shareEntityId = activeDiscussion?.entityId
  const shareSource = useMemo(() => shareEntityId ? discussionShareSource(api, shareEntityId) : null, [shareEntityId])
  const share = useShare(shareSource)
  const [shareDialogOpen, setShareDialogOpen] = useState(false)
  const [sessionInfoOpen, setSessionInfoOpen] = useState(false)
  const [titleEditing, setTitleEditing] = useState(false)
  const [attachSkillOpen, setAttachSkillOpen] = useState(false)
  const [attachMcpOpen, setAttachMcpOpen] = useState(false)
  const [reopenPending, setReopenPending] = useState(false)
  const [confidentialityPending, setConfidentialityPending] = useState(false)
  const [liveTimelinePending, setLiveTimelinePending] = useState(false)
  const [orchestratedPending, setOrchestratedPending] = useState(false)
  const [qualityTiers, setQualityTiers] = useState<QualityTierInfo[]>([])
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const providersRef = useRef(providers)
  providersRef.current = providers
  // Session Info's own reads also feed the Live quota ring, so the ring adds no provider calls.
  const recordedUsageLoader = useMemo(() => recordingUsageLoader(
    providerUsageStore,
    loadProviderUsage,
    provider => usageKey(provider, providersRef.current),
  ), [loadProviderUsage])
  const liveQuota = useMemo<LiveQuotaContextValue>(() => ({
    providers,
    load: loadProviderUsage,
    providerFor: live => resolveLiveProvider(
      live,
      getAgent(live.agentId)?.provider,
      live.id === activeDiscussion?.id ? sessionStats?.provider : null,
    ),
  }), [activeDiscussion?.id, getAgent, loadProviderUsage, providers, sessionStats?.provider])
  const [capturingContext, setCapturingContext] = useState(false)
  const capturingContextRef = useRef(false)
  const [contextPickerOpen, setContextPickerOpen] = useState(false)
  const [redLeafPreview, setRedLeafPreview] = useState<VisibleAppContext | null>(null)
  const [monitorSources, setMonitorSources] = useState<MonitorVisualSource[]>([])
  const [monitorSourcesLoading, setMonitorSourcesLoading] = useState(false)
  const [monitorSourcesError, setMonitorSourcesError] = useState<string | null>(null)

  useEffect(() => {
    setSessionInfoOpen(false)
    setTitleEditing(false)
  }, [activeDiscussionId])

  useEffect(() => {
    api.get<{ tiers: QualityTierInfo[] }>("/ai-session/quality-modes")
      .then(data => setQualityTiers(data.tiers ?? []))
      .catch(() => {})
    api.get<ProviderInfo[]>("/ai-session/providers/configured")
      .then(data => setProviders(Array.isArray(data) ? data : []))
      .catch(() => {})
  }, [])

  const attachmentTransport = useMemo<AttachmentTransport>(() => ({
    async upload(file) {
      const form = new FormData()
      form.append("file", file, file.name)
      const response = await novaExecution.fetch("/ai-session/input-attachments", { method: "POST", credentials: "include", body: form })
      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: response.statusText }))
        throw new Error(error.message ?? error.error ?? response.statusText)
      }
      return response.json() as Promise<UploadedAttachment>
    },
    async delete(attachmentId) {
      const response = await novaExecution.fetch(`/ai-session/input-attachments/${encodeURIComponent(attachmentId)}`, { method: "DELETE", credentials: "include" })
      if (!response.ok && response.status !== 404) throw new Error("The attachment could not be removed")
    },
    getDownloadUrl(attachment) { return attachment.downloadUrl },
  }), [])

  const attachVisibleContext = useCallback(async (
    discussionId: string,
    initialContext: VisibleAppContext,
    filename: string,
    screenshotAlreadyUnavailable = false,
  ) => {
    let context = initialContext
    let screenshotAttachment: UploadedAttachment | undefined
    let degraded = screenshotAlreadyUnavailable

    if (context.screenshot) {
      try {
        const blob = await fetch(`data:${context.screenshot.mediaType};base64,${context.screenshot.base64}`).then(response => response.blob())
        screenshotAttachment = await attachmentTransport.upload(new File([blob], filename, { type: context.screenshot.mediaType }))
      } catch {
        context = { ...context, screenshot: undefined }
        degraded = true
      }
    }

    pendingContext.set(discussionId, {
      context,
      screenshotAttachment,
      discard: screenshotAttachment
        ? () => { void attachmentTransport.delete(screenshotAttachment.id).catch(() => {}) }
        : undefined,
    })

    if (degraded) {
      toast({
        title: "Context attached without screenshot",
        description: "The source details and metadata are ready to send.",
        variant: "default",
      })
    }
  }, [attachmentTransport, pendingContext, toast])

  const captureWhatISee = useCallback(async (): Promise<UiSurfaceActionResult> => {
    const discussionId = activeDiscussionId
    if (!discussionId || !activeDiscussion) {
      return { ok: false, state: "open", error: { code: "discussion_required", message: "Select a discussion before attaching foreground context." } }
    }
    if (activeDiscussion.type === "heartbeat" || activeDiscussion.ownedBy || activeDiscussion.status === "archived" || activeDiscussion.status === "archiving") {
      return { ok: false, state: "open", error: { code: "discussion_read_only", message: "This discussion cannot accept a context attachment." } }
    }
    if (capturingContextRef.current) {
      return { ok: false, state: "open", error: { code: "capture_in_progress", message: "Foreground context is already being captured." } }
    }

    capturingContextRef.current = true
    setCapturingContext(true)
    try {
      const captured = await captureVisibleAppContext({ sourceWindow: window, sourceDocument: document })
      await attachVisibleContext(discussionId, captured.context, "what-i-see-redleaf.png", captured.screenshotStatus === "unavailable")
      setContextPickerOpen(false)
      return { ok: true, state: "open" }
    } catch (error) {
      const sourceChanged = error instanceof VisibleAppContextCaptureError && error.code === "source_changed"
      const message = sourceChanged
        ? "The foreground app changed during capture. Try once more on the page you want to share."
        : error instanceof Error ? error.message : "Foreground context could not be captured."
      toast({ title: sourceChanged ? "Screen changed" : "Context capture failed", description: message, variant: "error" })
      return {
        ok: false,
        state: "open",
        error: { code: sourceChanged ? "source_changed" : "capture_failed", message },
      }
    } finally {
      capturingContextRef.current = false
      setCapturingContext(false)
    }
  }, [activeDiscussion, activeDiscussionId, attachVisibleContext, toast])

  const captureMonitor = useCallback(async (sourceId: string): Promise<void> => {
    const discussionId = activeDiscussionId
    if (!discussionId || !activeDiscussion || capturingContextRef.current) return
    if (activeDiscussion.type === "heartbeat" || activeDiscussion.ownedBy || activeDiscussion.status === "archived" || activeDiscussion.status === "archiving") return

    capturingContextRef.current = true
    setCapturingContext(true)
    try {
      setContextPickerOpen(false)
      await new Promise<void>(resolve => {
        environment.window.requestAnimationFrame(() => environment.window.requestAnimationFrame(() => resolve()))
      })
      const capture = await captureMonitorVisualSource(sourceId)
      await attachVisibleContext(discussionId, monitorCaptureToContext(capture), `what-i-see-${capture.name.toLowerCase().replace(/\s+/g, "-")}.png`)
    } catch (error) {
      toast({
        title: "Monitor capture failed",
        description: error instanceof Error ? error.message : "The selected monitor could not be captured.",
        variant: "error",
      })
    } finally {
      capturingContextRef.current = false
      setCapturingContext(false)
    }
  }, [activeDiscussion, activeDiscussionId, attachVisibleContext, environment.window, toast])

  const refreshContextSources = useCallback(() => {
    const controller = new AbortController()
    setRedLeafPreview(null)
    setMonitorSources([])
    setMonitorSourcesLoading(true)
    setMonitorSourcesError(null)
    void Promise.allSettled([
      captureVisibleAppContext({ sourceWindow: window, sourceDocument: document })
        .then(captured => setRedLeafPreview(captured.context)),
      listMonitorVisualSources(controller.signal)
        .then(setMonitorSources)
        .catch(error => {
          setMonitorSources([])
          setMonitorSourcesError(error instanceof Error ? error.message : "Physical monitors are unavailable.")
        }),
    ]).finally(() => setMonitorSourcesLoading(false))
    return () => controller.abort()
  }, [])

  useEffect(() => {
    if (!contextPickerOpen) return
    return refreshContextSources()
  }, [contextPickerOpen, refreshContextSources])

  useEffect(() => {
    if (!floating) return
    const handleCaptureRequest = (event: Event) => {
      const request = (event as CustomEvent<FloatingNovaCaptureContextRequest>).detail
      void captureWhatISee().then(result => request?.respond?.(result))
    }
    environment.document.addEventListener(FLOATING_NOVA_CAPTURE_CONTEXT_EVENT, handleCaptureRequest)
    return () => environment.document.removeEventListener(FLOATING_NOVA_CAPTURE_CONTEXT_EVENT, handleCaptureRequest)
  }, [captureWhatISee, environment.document, floating])

  const prepareOutgoingMessage = useCallback((message: OutgoingMessageDraft): OutgoingMessageDraft => {
    if (!activeDiscussionId) return message
    const pending = pendingContext.consume(activeDiscussionId)
    if (!pending) return message
    return applyPendingVisibleContext({
      ...message,
      content: formatContextMessage(pending.context, message.content),
    }, pending)
  }, [activeDiscussionId, pendingContext])

  const activePendingContext = pendingContext.get(activeDiscussionId)

  const handleSend = useCallback((content: string, images?: ImageAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    return sendMessage(activeDiscussionId, content, images, options)
  }, [activeDiscussionId, sendMessage])

  // What a LIVE report needs around it: titles, workstreams (the sidebar groups) and a way to answer.
  const liveReportHost = useMemo<LiveReportHost>(() => ({
    discussions,
    groupOf: (discussionId) => {
      const discussion = discussions.find(d => d.id === discussionId)
      const label = discussion && settings.groupDiscussions ? discussionLabel(discussion, grouping.view) : undefined
      if (!label?.group) return null
      const visual = grouping.view?.groups?.find(group => group.name === label.group)
      return { name: label.group, icon: `ph-bold ph-${groupingIcon(visual?.icon)}` }
    },
    agent: null,
    reply: (text) => { void handleSend(text) },
  }), [discussions, grouping.view, handleSend, settings.groupDiscussions])

  const handleSendInput = useCallback((input: ChatInputPart[], attachments: UploadedAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    return sendInput(activeDiscussionId, input, attachments, options)
  }, [activeDiscussionId, sendInput])

  const queueTransport = useMemo<ChatQueueTransport | undefined>(() => activeDiscussionId ? ({
    list: () => api.get<ChatQueueSnapshot>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue?includeTerminal=true`),
    cancel: (itemId) => api.delete<ChatQueuedItem>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/${itemId}`),
    retry: (itemId) => api.post<ChatQueuedItem>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/${itemId}/retry`),
    steer: (itemId) => api.post(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/${itemId}/steer`),
    sendNow: async () => { await api.post(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/send-now`) },
    subscribe(listener) {
      const onUpdate = (event: Event) => {
        const detail = (event as CustomEvent<{ discussionId?: string }>).detail
        if (detail?.discussionId === activeDiscussionId) listener()
      }
      environment.window.addEventListener("nova:input-queue-updated", onUpdate)
      environment.window.addEventListener("nova:maintenance-refresh", listener)
      return () => {
        environment.window.removeEventListener("nova:input-queue-updated", onUpdate)
        environment.window.removeEventListener("nova:maintenance-refresh", listener)
      }
    },
  }) : undefined, [activeDiscussionId, environment.window])

  const handleInterrupt = useCallback(() => {
    if (!activeDiscussionId) return
    interruptDiscussion(activeDiscussionId)
  }, [activeDiscussionId, interruptDiscussion])

  // `answer` is the readable form; `payload` is what actually goes on the wire
  // (the picked labels, or freeform text typed instead of picking).
  const handleAnswerQuestion = useCallback((answer: string, payload?: QuestionAnswerPayload) => {
    if (!activeDiscussionId) return
    answerQuestion(activeDiscussionId, answer, payload)
  }, [activeDiscussionId, answerQuestion])

  // Rejects with the server's words (approval_requires_person...), which the card shows.
  const handlePermissionDecision = useCallback((payload: PermissionDecisionPayload) => {
    if (!activeDiscussionId) return Promise.reject(new Error("No discussion is open."))
    return decidePermission(activeDiscussionId, payload)
  }, [activeDiscussionId, decidePermission])

  const handleLoadEarlier = useCallback(() => {
    if (!activeDiscussionId) return
    return loadEarlierMessages(activeDiscussionId)
  }, [activeDiscussionId, loadEarlierMessages])

  // Artifacts: one panel per view that follows the open discussion. "Go to message" asks the chat
  // to centre a message and, while it is not loaded, reads earlier history through the same pager
  // that scrolling up uses.
  const [artifactsOpen, setArtifactsOpen] = useState(false)
  const goToMessage = useGoToMessage({
    conversationId: activeDiscussionId,
    hasEarlier: hasEarlierMessages && !earlierHistoryLoadFailure,
    isLoadingEarlier,
    loadEarlier: handleLoadEarlier,
    onMissing: () => toast({ variant: "error", title: "That message is no longer in this conversation" }),
  })
  const focusRequest = goToMessage.focusRequest
  const seekingEarlier = goToMessage.seeking
  // A link to one message: go there once its discussion is on screen, then drop the parameter, so
  // Back and a reload don't repeat it. Archived discussions come through the same selection.
  const linkedMessage = floating ? null : new URLSearchParams(location.search).get(MESSAGE_PARAM)
  const handledLink = useRef<string | null>(null)
  useEffect(() => {
    if (!linkedMessage) {
      handledLink.current = null
      return
    }
    const step = messageDeepLinkStep({
      uid: linkedMessage,
      conversationId: urlDiscussionId ?? null,
      activeConversationId: activeDiscussionId,
      loading: isLoadingMessages || isResolvingDiscussion,
      handled: handledLink.current,
    })
    if (step.action !== "go") return
    handledLink.current = step.handled
    setMobileTab(1)
    goToMessage.go(step.uid)
    navigate(
      { pathname: location.pathname, search: withoutMessageParam(location.search) },
      { replace: true, state: location.state },
    )
  }, [activeDiscussionId, goToMessage.go, isLoadingMessages, isResolvingDiscussion, linkedMessage, location.pathname, location.search, location.state, navigate, urlDiscussionId])
  const handleFocusMessageResult = goToMessage.onFocusMessageResult
  const closeArtifacts = useCallback(() => setArtifactsOpen(false), [])
  const openArtifacts = useCallback(() => setArtifactsOpen(true), [])
  const toggleArtifacts = useCallback(() => setArtifactsOpen(open => !open), [])
  const jumpToMessage = goToMessage.go

  const handleRetryHistory = useCallback(() => {
    if (!activeDiscussionId) return
    return retryHistoryLoad(activeDiscussionId)
  }, [activeDiscussionId, retryHistoryLoad])

  const handleSelectDiscussion = useCallback((id: string) => {
    if (id !== activeDiscussionId && !journalOpen) {
      const pane = discussionPaneRef.current
      const fadeLayer = transcriptFadeRef.current
      const input = pane?.querySelector(COMPOSER_INPUT)
      restoreComposerFocusRef.current = input && input === pane?.ownerDocument.activeElement ? id : null
      if (pane && fadeLayer && !environment.window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
        fadeOutgoingTranscript(pane, fadeLayer)
      }
    }

    // The pending ID masks the previous route until the router or Float host catches up.
    setPendingDiscussionId(id)
    if (floating) onSelectDiscussion?.(id)
    else navigate(`/apps/nova/chat/${id}`)
    setMobileTab(1)
  }, [activeDiscussionId, environment.window, floating, journalOpen, navigate, onSelectDiscussion])

  const handleRotateDiscussion = useCallback(async (id: string) => {
    const followReplacement = activeDiscussionId === id
    const newDiscussionId = await rotateDiscussion(id)
    if (followReplacement && newDiscussionId) handleSelectDiscussion(newDiscussionId)
  }, [activeDiscussionId, handleSelectDiscussion, rotateDiscussion])

  // Semantic events navigate only this router/client. Never round-trip through
  // the global codered.navigate WebSocket event for delegation markers.
  const resolveEventLink = useCallback((event: ParsedEvent) => {
    if (event.key === "discussion" || event.key === AGENT_NOTE_KEY) {
      const discussionId = event.data?.discussionId
      if (typeof discussionId !== "string" || !discussionId) return undefined
      return () => handleSelectDiscussion(discussionId)
    }
    // The marker and the delegated-session notices (status, approval waiting) both open the worker.
    if (event.key === "delegation" || event.key === "delegate") {
      const path = delegationEventPath(event.key, event.data)
      return path ? () => navigate(path) : undefined
    }
    return undefined
  }, [handleSelectDiscussion, navigate])

  const openNewDiscussion = useCallback(() => {
    if (onNewDiscussion) onNewDiscussion()
    else environment.window.dispatchEvent(new CustomEvent("nova:open-new-discussion"))
  }, [environment.window, onNewDiscussion])

  useEffect(() => {
    if (!floating) return

    const runNavigation = (action: FloatingNovaNavigationAction) => {
      if (action === "show-discussions") {
        setMobileTab(0)
        return
      }
      if (action === "show-chat") {
        if (activeDiscussionId) setMobileTab(1)
        return
      }
      if (action === "new-discussion") {
        openNewDiscussion()
        return
      }
      const direction = action === "next-discussion" ? 1 : -1
      const adjacent = getAdjacentSidebarDiscussion(discussions, activeDiscussionId, direction, agentFilter, grouping.view, collapsedDiscussionGroups, groupSort)
      if (adjacent) handleSelectDiscussion(adjacent.id)
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented || environment.document.querySelector('[data-slot="dialog-content"]')) return
      const action = getFloatingNovaNavigationAction(event)
      if (!action) return
      event.preventDefault()
      event.stopPropagation()
      runNavigation(action)
    }
    const handleNavigationEvent = (event: Event) => {
      const action = (event as CustomEvent<{ action?: FloatingNovaNavigationAction }>).detail?.action
      if (action) runNavigation(action)
    }

    environment.document.addEventListener("keydown", handleKeyDown)
    environment.document.addEventListener(FLOATING_NOVA_NAVIGATION_EVENT, handleNavigationEvent)
    return () => {
      environment.document.removeEventListener("keydown", handleKeyDown)
      environment.document.removeEventListener(FLOATING_NOVA_NAVIGATION_EVENT, handleNavigationEvent)
    }
  }, [activeDiscussionId, agentFilter, collapsedDiscussionGroups, discussions, grouping.view, groupSort, environment.document, floating, handleSelectDiscussion, openNewDiscussion])

  const handleConfidentialToggle = useCallback(async (checked: boolean) => {
    if (!activeDiscussionId || confidentialityPending) return
    setConfidentialityPending(true)
    try {
      await setConfidential(activeDiscussionId, checked)
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not update confidentiality",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    } finally {
      setConfidentialityPending(false)
    }
  }, [activeDiscussionId, confidentialityPending, setConfidential, toast])

  const handleLiveTimelineToggle = useCallback(async (checked: boolean) => {
    if (!activeDiscussionId || liveTimelinePending) return
    setLiveTimelinePending(true)
    try {
      await setLiveTimeline(activeDiscussionId, checked)
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not update the LIVE timeline setting",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    } finally {
      setLiveTimelinePending(false)
    }
  }, [activeDiscussionId, liveTimelinePending, setLiveTimeline, toast])

  const handleOrchestratedToggle = useCallback(async (checked: boolean) => {
    if (!activeDiscussionId || orchestratedPending) return
    setOrchestratedPending(true)
    try {
      await setOrchestrated(activeDiscussionId, checked)
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not update Orchestrated",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    } finally {
      setOrchestratedPending(false)
    }
  }, [activeDiscussionId, orchestratedPending, setOrchestrated, toast])

  const [runOnPending, setRunOnPending] = useState(false)
  const handleRunOnChange = useCallback(async (nodeId: string | null) => {
    if (!activeDiscussionId || runOnPending) return
    setRunOnPending(true)
    try {
      await setRunOnNode(activeDiscussionId, nodeId)
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not change where new sessions run",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    } finally {
      setRunOnPending(false)
    }
  }, [activeDiscussionId, runOnPending, setRunOnNode, toast])

  const handleShare = useCallback(() => {
    share.reset()
    share.createShare()
    setShareDialogOpen(true)
  }, [share])

  const handleRetryDiscussionList = useCallback(() => {
    if (archiveMode) void refreshArchivedDiscussions(archiveQuery, agentFilter).catch(() => {})
    else syncAndRefresh().catch(() => {})
  }, [agentFilter, archiveMode, archiveQuery, refreshArchivedDiscussions, syncAndRefresh])

  const handleToggleArchiveMode = useCallback(() => {
    setArchiveMode(!archiveMode)
  }, [archiveMode, setArchiveMode])

  const handleExitArchiveMode = useCallback(() => {
    setArchiveMode(false)
  }, [setArchiveMode])

  const handleLoadMoreArchived = useCallback(() => {
    void refreshArchivedDiscussions(archiveQuery, agentFilter, true).catch(() => {})
  }, [agentFilter, archiveQuery, refreshArchivedDiscussions])

  const handleReopen = useCallback(async () => {
    if (!activeDiscussionId || reopenPending) return
    setReopenPending(true)
    try {
      await reopenDiscussion(activeDiscussionId)
    } finally {
      setReopenPending(false)
    }
  }, [activeDiscussionId, reopenDiscussion, reopenPending])

  const titleUpdate = useTitleUpdate(updateDiscussionTitle, "New discussion", activeDiscussionId)
  const handleUpdateTitle = titleUpdate.run
  const titleUpdatePending = titleUpdate.pending

  const handleCloseDiscussion = useCallback((id: string) => {
    if (id !== activeDiscussionId) {
      archiveDiscussion(id)
      return
    }
    // Closing the open discussion leaves it, wherever the close came from;
    // Undo brings the user back to it.
    archiveDiscussion(id, { onUndo: () => navigate(`/apps/nova/chat/${id}`) })
    navigate("/apps/nova/chat")
  }, [activeDiscussionId, archiveDiscussion, navigate])

  const handleFloatDiscussion = useCallback((id: string) => {
    void runUiSurfaceAction("nova:floating-chat", "open", { discussionId: id })
  }, [])

  // Sidebar row actions that drive the open discussion's header (rename, Info,
  // share) open that discussion first and run once it is the active one.
  // Another app owns this discussion (a Storyboard campaign Agent thread): Nova shows
  // its transcript but never writes to it or sends into its session.
  const owningApp = activeDiscussion?.ownedBy ? formatOwningApp(activeDiscussion.ownedBy.app) : null
  const voiceAvailable = !!activeDiscussion && !discussionClosed && !owningApp && activeDiscussion.status !== "stopped"
    && (voiceSession.phase === "idle" || voiceSession.phase === "closed")
  const openVoice = useCallback(() => { voiceSession.open() }, [voiceSession])
  const openSessionInfo = useCallback(() => {
    void refreshDelegationActivity(true)
    setSessionInfoOpen(true)
  }, [refreshDelegationActivity])

  // Read RedLeaf's policy each time Info opens, so Full turned on in Settings shows next time.
  const fullAccessEnabled = useFullAccessEnabled(sessionInfoOpen)
  const activeSessionId = activeDiscussion?.sessionId ?? null
  // A read-only discussion changes nothing.
  const accessChangeable = !!activeSessionId && !discussionClosed && !owningApp
  const handleAccessLevelChange = useCallback(async (requested: AccessLevel) => {
    if (!activeSessionId) throw new Error("This discussion has no session yet.")
    // A refusal rejects with RedCompute's message, shown under the Permissions row.
    const level = await changeAccessLevel(api.post, activeSessionId, requested)
    applyAccessLevel(activeSessionId, level)
  }, [activeSessionId, applyAccessLevel])
  const activeDelegations = activeDiscussion ? delegationActivity[activeDiscussion.id] : undefined
  // Skills, workspace, memory and node: read each time Info opens.
  const infoSources = useSessionInfoSources({
    open: sessionInfoOpen,
    agentId: activeAgent?.id,
    workspaceId: activeAgent?.workspaceId,
    sessionNodeId,
    folder: sessionProjectPath,
  })
  const infoNode = infoSources.node
  // Discussion grouping, as the sidebar row uses it: no label (grouping off, confidential, closed or a
  // stale title) changes nothing in the hero.
  const groupLabel = activeDiscussion && settings.groupDiscussions ? discussionLabel(activeDiscussion, grouping.view) : undefined
  const groupVisual = groupLabel?.group ? grouping.view?.groups?.find(group => group.name === groupLabel.group) : undefined
  const sessionInfoProps = {
    stats: sessionStats,
    messages: activeMessages,
    agent: activeAgent ? {
      id: activeAgent.id,
      name: activeAgent.name,
      avatarUrl: activeAgent.avatarUrl,
      href: getEntityHref("agent", activeAgent.id),
    } : null,
    qualityTierOptions: qualityTiers.map(t => ({ value: t.slug, label: t.label, color: t.color, icon: t.icon })),
    providerOptions: providers.map(p => ({
      value: p.slug,
      aliases: [p.backend],
      label: p.name,
      color: p.color,
      icon: p.icon,
      iconSvgPath: p.iconSvgPath,
    })),
    loadProviderUsage: recordedUsageLoader,
    // Only the Permissions row: Nova can't change a running session's model or
    // quality from Info, so no other row becomes a picker (no onConfigChange).
    onAccessLevelChange: accessChangeable ? handleAccessLevelChange : undefined,
    fullAccessEnabled,
    // The floating window has no RedLeaf shell to open Settings in: the link stays text there.
    fullAccessSettingsHref: floating ? undefined : FULL_ACCESS_SETTINGS_HREF,
    subtitle: groupLabel && settings.refineDiscussionTitles && groupLabel.label ? groupLabel.label : undefined,
    titleLeading: activeDiscussion && groupLabel ? (
      <DiscussionThumbnail id={activeDiscussion.id} icon={groupingIcon(groupLabel.icon)}
        target={settings.useDiscussionThumbnails ? groupLabel.thumbnailTarget : undefined} />
    ) : undefined,
    group: groupLabel?.group ? { name: groupLabel.group, icon: `ph ph-${groupingIcon(groupVisual?.icon)}` } : undefined,
    skills: infoSources.skills,
    node: infoNode,
    workspace: infoSources.workspace,
    memory: infoSources.memory,
    // The messages loaded so far are all the "used in this discussion" lists can count.
    historyPartial: hasEarlierMessages,
    // With a choice of nodes, the discussion's "run on" setting sits under the node. With one node
    // the node section already says every session runs there.
    nodeExtra: activeDiscussion && runOnAvailable && !owningApp && infoNode && !infoNode.onlyNode ? (
      <DiscussionRunOnSetting
        key={activeDiscussion.id}
        title="Run new sessions on"
        nodeId={activeDiscussion.nodeId ?? null}
        sessionNodeId={sessionNodeId}
        hasSession={!!activeDiscussion.sessionId}
        disabled={runOnPending || discussionClosed}
        onChange={nodeId => void handleRunOnChange(nodeId)}
      />
    ) : undefined,
    workspaceExtra: activeDiscussion ? (
      <>
        {!!activeDiscussion.worktrees?.length && (
          <div>
            <h3 className="mb-2 text-xs font-medium text-text-muted">Worktrees</h3>
            <WorktreeChips worktrees={activeDiscussion.worktrees} bare />
          </div>
        )}
        <DelegationsInfo activity={activeDelegations} />
      </>
    ) : undefined,
  }

  const [pendingRowAction, setPendingRowAction] = useState<{ id: string; action: DiscussionRowAction } | null>(null)
  const handleRowAction = useCallback((id: string, action: DiscussionRowAction) => {
    setPendingRowAction({ id, action })
    if (id !== activeDiscussionId) handleSelectDiscussion(id)
  }, [activeDiscussionId, handleSelectDiscussion])
  useEffect(() => {
    if (!pendingRowAction || activeDiscussion?.id !== pendingRowAction.id) return
    setPendingRowAction(null)
    setMobileTab(1)
    if (pendingRowAction.action === "info") openSessionInfo()
    else if (pendingRowAction.action === "artifacts") openArtifacts()
    else if (pendingRowAction.action === "rename") setTitleEditing(true)
    else if (pendingRowAction.action === "voice") openVoice()
    else handleShare()
  }, [activeDiscussion?.id, handleShare, openArtifacts, openSessionInfo, openVoice, pendingRowAction])

  // Desktop shortcuts for the open discussion; the menus show the same keys.
  const canManageDiscussion = activeDiscussion?.type === "chat" && !discussionClosed && !owningApp
  // A Skill for this discussion only: any open discussion a person talks in, LIVE included.
  const canAttachSkills = !!activeDiscussion && activeDiscussion.type !== "heartbeat" && !discussionClosed && !owningApp

  const liveViews = findLiveHeartbeatPair(discussions, journalOpen ? journalLive : activeDiscussion)
  // Without the Journal tab, a lone Live has nothing to switch to.
  const liveHeartbeatPair = liveViews && (!floating || liveViews.heartbeat) ? liveViews : null

  const liveHeartbeatTabs = liveHeartbeatPair && (
    <Tabs
      value={journalOpen ? "journal" : activeDiscussion?.type === "heartbeat" ? "heartbeat" : "live"}
      onValueChange={(value) => {
        if (value === "journal") {
          const agentId = liveHeartbeatPair.live.agentId
          navigate(journalPath({ agent: agentId && agentId !== defaultAgentId ? agentId : null }))
          return
        }
        handleSelectDiscussion(value === "heartbeat" && liveHeartbeatPair.heartbeat ? liveHeartbeatPair.heartbeat.id : liveHeartbeatPair.live.id)
      }}
      className="shrink-0"
      style={{ height: "100%" }}
    >
      <TabsList
        variant="line"
        aria-label="Live views"
        style={{ height: "100%", padding: 0 }}
      >
        <TabsTrigger
          value="live"
          className={LIVE_TAB_CLASS}
          style={{ height: "100%" }}
        >
          <i
            aria-hidden="true"
            className={`ph-bold ph-broadcast text-[12px] ${liveHeartbeatPair.live.status === "thinking" ? "animate-pulse" : ""}`}
            style={{ color: "var(--color-status-live)" }}
          />
          Live
        </TabsTrigger>
        {liveHeartbeatPair.heartbeat && (
          <TabsTrigger
            value="heartbeat"
            aria-label={liveHeartbeatPair.heartbeat.status === "thinking" ? "Heartbeat, tick running" : "Heartbeat"}
            className={LIVE_TAB_CLASS}
            style={{ height: "100%" }}
          >
            <i
              aria-hidden="true"
              className={`ph-bold ph-heartbeat text-[12px] ${liveHeartbeatPair.heartbeat.status === "thinking" ? "animate-pulse" : ""}`}
              style={{ color: "var(--color-accent-gold)" }}
            />
            Heartbeat
          </TabsTrigger>
        )}
        {!floating && (
          <TabsTrigger
            value="journal"
            className={LIVE_TAB_CLASS}
            style={{ height: "100%" }}
          >
            <i
              aria-hidden="true"
              className="ph-bold ph-notebook text-[12px]"
              style={{ color: "var(--color-accent-purple)" }}
            />
            Journal
          </TabsTrigger>
        )}
      </TabsList>
    </Tabs>
  )

  // The Agent's Heartbeat is what picks an Orchestrated discussion up: without an open one, nothing would.
  const agentHasHeartbeat = !!activeDiscussion && heartbeatAgents.has(activeDiscussion.agentId)
  const effective = activeDiscussion ? effectiveBehaviours(activeDiscussion, agentHasHeartbeat) : null
  const behaviours: BehaviourItem[] = !activeDiscussion || !effective ? [] : [
    ...(hasIntrinsicBehaviours(activeDiscussion) ? [] : [{
      key: "orchestrated",
      icon: BEHAVIOUR_ICONS.orchestrated,
      title: "Orchestrated",
      accent: "var(--color-accent-red)",
      description: "When it goes idle, the Heartbeat picks it up and decides what's next",
      checked: effective.orchestrated,
      pending: orchestratedPending,
      blocked: activeDiscussion.confidential ? "Not while Confidential: the Heartbeat would read it"
        : !agentHasHeartbeat ? "Its Agent has no Heartbeat, so nothing would pick it up"
        : null,
      status: <OrchestrationStatus discussion={activeDiscussion} />,
      settingPath: "discussion.orchestrated",
      onChange: handleOrchestratedToggle,
    }]),
    {
      key: "liveTimeline",
      icon: BEHAVIOUR_ICONS.liveTimeline,
      title: "LIVE timeline",
      accent: "var(--color-accent-teal)",
      description: "Recent LIVE events (mail, automations, lights, music, other discussions) in its context",
      checked: effective.liveTimeline,
      pending: liveTimelinePending,
      settingPath: "discussion.liveTimeline",
      onChange: handleLiveTimelineToggle,
    },
    {
      key: "confidential",
      icon: BEHAVIOUR_ICONS.confidential,
      title: "Confidential",
      accent: "var(--color-accent-gold)",
      description: "Kept out of LIVE, the Heartbeat, other discussions and bulk exports; the share control is hidden",
      checked: effective.confidential,
      pending: confidentialityPending,
      settingPath: "discussion.confidential",
      onChange: handleConfidentialToggle,
    },
  ]
  const sessionInfoContent = activeDiscussion && (
    <DiscussionBehaviourList items={behaviours} closed={discussionClosed} />
  )

  const chatHeader = activeDiscussion && (
    <>
    <PanelHeader
      leading={
        liveHeartbeatTabs ? <div className="flex flex-1 min-w-0 self-stretch">{liveHeartbeatTabs}</div> : discussionClosed || owningApp ? (
          <span className="truncate text-sm font-medium text-contrast">{activeDisplayTitle || (activeDiscussion.type === "heartbeat" ? "Heartbeat" : "Untitled discussion")}</span>
        ) : (
          <EditableTitle
            title={activeDisplayTitle || "New discussion"}
            onRename={(title) => renameDiscussion(activeDiscussion.id, title)}
            editing={titleEditing}
            onEditingChange={setTitleEditing}
            inputLabel="Discussion title"
            buttonTitle="Rename discussion"
            name="discussion-title"
          />
        )
      }
    >
      {!floating && (
        <>
          {/* The controlled modal portals outside this hidden legacy trigger. */}
          {/* Capture sees clicks in the portalled modal (React events follow the tree). */}
          <div className="hidden" onClickCapture={openSettingsFromLink}>
            <SessionInfoButton
              {...sessionInfoProps}
              open={sessionInfoOpen}
              onOpenChange={setSessionInfoOpen}
            >
              {sessionInfoContent}
            </SessionInfoButton>
          </div>
          <ConversationActionsMenu
            triggerRef={voiceSessionTriggerRef}
            entries={discussionActionEntries({
              showShortcuts: true,
              onVoice: owningApp ? undefined : openVoice,
              voiceDisabled: !voiceAvailable,
              onAttachSkill: canAttachSkills ? () => setAttachSkillOpen(true) : undefined,
              onAttachMcpServer: canAttachSkills ? () => setAttachMcpOpen(true) : undefined,
              onInfo: openSessionInfo,
              onArtifacts: openArtifacts,
              onRename: canManageDiscussion ? () => setTitleEditing(true) : undefined,
              onUpdateTitle: canManageDiscussion ? () => void handleUpdateTitle(activeDiscussion.id) : undefined,
              updateTitlePending: titleUpdatePending,
              onShare: !discussionClosed && !owningApp && !activeDiscussion.confidential ? handleShare : undefined,
              onFloat: floatingSurface?.supported ? () => handleFloatDiscussion(activeDiscussion.id) : undefined,
              onReset: activeDiscussion.type === "live" ? () => void handleRotateDiscussion(activeDiscussion.id) : undefined,
              onStop: canManageDiscussion && activeDiscussion.sessionId && activeDiscussion.status !== "stopped" ? () => void stopDiscussion(activeDiscussion.id) : undefined,
              onClose: canManageDiscussion ? () => handleCloseDiscussion(activeDiscussion.id) : undefined,
            })}
          />
        </>
      )}
      {floating && onDock && (
        <ChatHeaderAction
          onClick={onDock}
          icon="ph-bold ph-arrow-square-in"
          label="Dock"
          data-ui-surface="nova:floating-chat"
          data-ui-action="dock"
          title="Dock in RedLeaf"
        />
      )}
      {floating && (
        <SessionInfoButton {...sessionInfoProps}>
          {sessionInfoContent}
        </SessionInfoButton>
      )}
    </PanelHeader>
      <WorktreeChips worktrees={activeDiscussion.worktrees} />
      <SkillChips
        discussionId={activeDiscussion.id}
        skills={activeDiscussion.skills}
        requests={activeDiscussion.skillRequests}
        mcpServers={activeDiscussion.mcpServers}
        mcpRequests={activeDiscussion.mcpServerRequests}
        agentName={activeAgent?.name}
        disabled={!canAttachSkills}
      />
      {canAttachSkills && (
        <AttachSkillDialog
          discussionId={activeDiscussion.id}
          agentName={activeAgent?.name}
          open={attachSkillOpen}
          onOpenChange={setAttachSkillOpen}
          onError={message => toast({ variant: "error", title: "Skill not attached", description: message })}
        />
      )}
      {canAttachSkills && (
        <AttachSkillDialog
          discussionId={activeDiscussion.id}
          kind="mcp"
          agentName={activeAgent?.name}
          open={attachMcpOpen}
          onOpenChange={setAttachMcpOpen}
          onError={message => toast({ variant: "error", title: "MCP server not attached", description: message })}
        />
      )}
      {earlierHistoryLoadFailure && (
        <div
          role="alert"
          data-slot="history-earlier-error"
          className="shrink-0 border-b border-destructive-a20 bg-destructive-a10 px-4 py-2 text-destructive"
        >
          <div className="mx-auto flex max-w-3xl items-center justify-center gap-2 text-xs">
            <i aria-hidden="true" className="ph-bold ph-warning-circle shrink-0 text-sm" />
            <span className="min-w-0">Couldn&apos;t load earlier messages.</span>
            <button
              type="button"
              onClick={handleLoadEarlier}
              disabled={isLoadingEarlier}
              className="shrink-0 rounded-md px-2 py-1 font-semibold text-destructive transition-colors hover:bg-destructive-a10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-destructive-a40 disabled:opacity-50"
            >
              {isLoadingEarlier ? "Retrying…" : "Retry"}
            </button>
          </div>
        </div>
      )}
      {historyLoadFailure && activeMessages.length > 0 && (
        <div
          role="alert"
          data-slot="history-partial-error"
          className="shrink-0 border-b border-destructive-a20 bg-destructive-a10 px-4 py-2 text-destructive"
        >
          <div className="mx-auto flex max-w-3xl items-center justify-center gap-2 text-xs">
            <i aria-hidden="true" className="ph-bold ph-warning-circle shrink-0 text-sm" />
            <span className="min-w-0">Couldn&apos;t load this discussion&apos;s history. {historyLoadFailure.message}</span>
            <button
              type="button"
              onClick={handleRetryHistory}
              disabled={isLoadingMessages}
              className="shrink-0 rounded-md px-2 py-1 font-semibold text-destructive transition-colors hover:bg-destructive-a10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-destructive-a40 disabled:opacity-50"
            >
              {isLoadingMessages ? "Retrying…" : "Retry"}
            </button>
          </div>
        </div>
      )}
    </>
  )

  // Where the dock exists (docked, or inline from 768px) nothing in this row ever wraps:
  // the header adapts to the width it really has (the dock-header container: the dock's
  // slot after the grip, or this header itself inline). Thresholds are the measured one-line widths, rounded up: with the agent
  // label 293.1px, so the label shows from 294px; with "New" labelled 230.3px (231px);
  // with both as icons 200.7px (201px), below which the actions tighten to 188.7px.
  // Docked at the list minimum (240px) the header has 196px, so the title only
  // truncates below the list minimum.
  const sidebarTitle = (
    <span className="text-[14px] font-medium text-contrast flex-1 @min-[0px]/dock-header:min-w-0 @min-[0px]/dock-header:truncate">
      {archiveMode ? "Archives" : "Discussions"}
    </span>
  )
  const sidebarHeader = (
    <PanelHeader
      // The dock-header container exists only where the dock does (docked, the dock's
      // slot; inline from 768px, this header). Phones (tabs) and Floating Nova have no
      // container, so every rule gated on it is off and the header renders as before.
      className="nova-discussions-header"
      // Back is its own slot, so it stays in the row when the dock tabs this list.
      back={archiveMode ? (
        <button
          type="button"
          onClick={handleExitArchiveMode}
          aria-label="Back to discussions"
          title="Back to discussions"
          className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
        >
          <i aria-hidden="true" className="ph-bold ph-arrow-left text-base" />
        </button>
      ) : undefined}
      leading={sidebarTitle}
    >
      {multiAgent && (
        <AgentPicker
          agents={agents}
          selectedId={agentFilter}
          onSelect={(id) => setSettings({ agentFilter: id })}
          showAll
          collapsible
        />
      )}
      {!archiveMode && (
        <button
          type="button"
          onClick={openNewDiscussion}
          className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10 @min-[0px]/dock-header:shrink-0 @min-[0px]/dock-header:whitespace-nowrap @max-[201px]/dock-header:px-1.5"
          title={`New discussion (${floating ? "Alt+N" : "Ctrl+N"})`}
          aria-label="New discussion"
        >
          <i className="ph-bold ph-plus text-xs" />
          <span className="@max-[231px]/dock-header:hidden">New</span>
        </button>
      )}
      <DropdownMenu>
        <DropdownMenuTrigger
          className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 @max-[201px]/dock-header:w-6"
          aria-label="More discussion options"
          title="More"
        >
          <i aria-hidden="true" className="ph-bold ph-dots-three-vertical text-base" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" sideOffset={4} className="max-h-[var(--available-height)] overflow-y-auto">
          {!archiveMode && (
            <>
              <DropdownMenuGroup>
                <DropdownMenuLabel>Grouping</DropdownMenuLabel>
                <DropdownMenuCheckboxItem checked={!!settings.groupDiscussions} onCheckedChange={checked => setSettings({ groupDiscussions: checked === true })}>
                  Group by project
                </DropdownMenuCheckboxItem>
                <div className="ms-3 border-s border-border ps-1">
                  <DropdownMenuCheckboxItem
                    checked={settings.refineDiscussionTitles}
                    disabled={!settings.groupDiscussions}
                    onCheckedChange={checked => setSettings({ refineDiscussionTitles: checked === true })}
                  >
                    Refine titles
                  </DropdownMenuCheckboxItem>
                  <DropdownMenuCheckboxItem
                    checked={settings.useDiscussionThumbnails}
                    disabled={!settings.groupDiscussions}
                    onCheckedChange={checked => setSettings({ useDiscussionThumbnails: checked === true })}
                  >
                    Use thumbnail images
                  </DropdownMenuCheckboxItem>
                  <DropdownMenuLabel>Sort groups</DropdownMenuLabel>
                  <DropdownMenuRadioGroup
                    aria-label="Sort groups"
                    value={discussionGroupSort}
                    onValueChange={value => setSettings({ discussionGroupSort: value as DiscussionGroupSortMode })}
                  >
                    {discussionGroupSortOptions.map(({ value, label }) => (
                      <DropdownMenuRadioItem key={value} value={value} disabled={!settings.groupDiscussions}>
                        {label}
                      </DropdownMenuRadioItem>
                    ))}
                  </DropdownMenuRadioGroup>
                  <DropdownMenuLabel>Sort discussions</DropdownMenuLabel>
                  <DropdownMenuRadioGroup
                    aria-label="Sort discussions"
                    value={groupedDiscussionSort}
                    onValueChange={value => setSettings({ groupedDiscussionSort: value as GroupedDiscussionSortMode })}
                  >
                    {groupedDiscussionSortOptions.map(({ value, label }) => (
                      <DropdownMenuRadioItem key={value} value={value} disabled={!settings.groupDiscussions}>
                        {label}
                      </DropdownMenuRadioItem>
                    ))}
                  </DropdownMenuRadioGroup>
                </div>
              </DropdownMenuGroup>
              <DropdownMenuSeparator />
            </>
          )}
          <DropdownMenuItem onClick={handleToggleArchiveMode}>
            <i
              aria-hidden="true"
              className={`${archiveMode ? "ph-bold ph-arrow-left" : "ph-bold ph-archive"} size-4`}
            />
            {archiveMode ? "Back to discussions" : "Browse archives"}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </PanelHeader>
  )

  // Delegations are host work in the chat's one activity row, after the
  // session, its agents and its background tasks.
  const delegationRowActivity = useMemo(() => delegationActivityGroup(activeDelegations), [activeDelegations])
  const hostRowActivity = useMemo(() => withWaitingActivity(delegationRowActivity, pendingQuestion), [delegationRowActivity, pendingQuestion])
  const handleStopBackgroundTask = useCallback((taskId: string) =>
    activeDiscussionId ? stopBackgroundTask(activeDiscussionId, taskId) : undefined,
  [activeDiscussionId, stopBackgroundTask])
  // Asks an unattended session denied: small notes under the newest assistant message, not cards and
  // not answerable. Live only; the denial also reaches the transcript as that tool call's result.
  const renderPermissionNotices = useCallback((_block: MessageBlock, index: number) => {
    if (!permissionNotices?.length) return null
    if (index !== activeMessages.findLastIndex(message => message.role === "assistant")) return null
    return (
      <div data-testid="permission-denied-notices" className="mt-1 space-y-0.5">
        {permissionNotices.map((notice, i) => (
          <p key={i} className="flex items-start gap-1.5 text-xs text-text-muted">
            <i className="ph-bold ph-prohibit mt-0.5 text-amber-400" aria-hidden />
            <span>{permissionNoticeText(notice)}</span>
          </p>
        ))}
      </div>
    )
  }, [permissionNotices, activeMessages])
  const renderStatusLine = useCallback(({ isStreaming, isReconnecting, messages }: { isStreaming: boolean; isReconnecting: boolean; messages: import("@redbamboo/chat").MessageBlock[] }) => (
    <NovaStatusLine isStreaming={isStreaming} isReconnecting={isReconnecting} messages={messages} />
  ), [])

  // Message senders carry the Agent id; Agent notes know their sender only by
  // the slug in their source, and slugs never take the shape of an id.
  const resolveAgentInfo = useCallback((agentRef: string) => {
    const agent = getAgent(agentRef) ?? agents.find(candidate => candidate.slug === agentRef)
    if (!agent) return undefined
    return { name: agent.name, avatarUrl: agent.avatarUrl }
  }, [agents, getAgent])

  // Read from what this client already lists, so a confidential origin it may
  // not read has no title to show, and a retitle shows without reloading history.
  const resolveNoteDiscussion = useCallback((id: string): (NoteOriginDiscussion & { summary: DiscussionInfo }) | undefined => {
    const open = discussions.find(candidate => candidate.id === id)
    const known = open ?? archivedDiscussions.find(candidate => candidate.id === id)
    if (!known) return undefined
    const closed = known.status === "archived" || known.status === "archiving"
    return {
      title: createDiscussionTitleResolver([...discussions, ...archivedDiscussions])(known),
      agentId: known.agentId ?? null,
      archived: !open || closed,
      summary: known,
    }
  }, [archivedDiscussions, discussions])

  // A note held for the session's next turn is in the discussion but not in the conversation yet.
  const resolveHeldEvent = useCallback<ResolveEventQueueStatus>(part => {
    const uid = eventInputMessageUid(part)
    return uid && heldEventUids?.has(uid) ? HELD_EVENT_STATUS : undefined
  }, [heldEventUids])

  const resolveAgentNote = useCallback((event: ParsedEvent) => {
    const presentation = describeAgentNote(event, {
      currentDiscussionId: activeDiscussionId,
      resolveAgent: resolveAgentInfo,
      resolveDiscussion: resolveNoteDiscussion,
      onOpenDiscussion: handleSelectDiscussion,
    })
    if (!presentation || !activeDiscussion || activeDiscussion.id !== activeDiscussionId) return presentation
    const sent = event.key === "agent-note-sent"
    const counterpart = sent ? event.data?.targetDiscussionId : event.data?.discussionId
    if (typeof counterpart !== "string" || !counterpart || counterpart === activeDiscussion.id) return presentation
    const other = resolveNoteDiscussion(counterpart)
    return {
      ...presentation,
      renderExchange: ({ part, ...slot }) => (
        <DiscussionThumbnailCacheContext.Provider value={thumbnails}>
          <AgentNoteExchange
            key={activeDiscussion.id + ":" + counterpart}
            {...slot}
            current={{
              id: activeDiscussion.id,
              title: activeDisplayTitle ?? "Untitled discussion",
              agentId: activeDiscussion.agentId,
              summary: activeDiscussion,
              canOpen: true,
            }}
            other={{
              id: counterpart,
              title: presentation.discussion,
              agentId: other?.agentId ?? null,
              summary: other?.summary.confidential && !activeDiscussion.confidential ? undefined : other?.summary,
              canOpen: !!presentation.discussionHref,
            }}
            clicked={{
              uid: part.messageUid ?? part.messageId ?? "opened-note",
              direction: sent ? "sent" : "received",
              content: event.text,
              timestamp: event.postedAt ?? event.timestamp,
              agentId: sent ? activeDiscussion.agentId : typeof event.data?.agent === "string" ? event.data.agent : other?.agentId ?? null,
              failed: sent && event.data?.failed === true,
            }}
            onOpenDiscussion={handleSelectDiscussion}
            resolveAgent={resolveAgentInfo}
            resolveImageSrc={resolveImageSrc}
            resolveFileLink={resolveFileLink}
            visualSettings={{
              grouping: grouping.view,
              showAvatars: settings.showDiscussionAvatars,
              useThumbnails: settings.useDiscussionThumbnails,
            }}
          />
        </DiscussionThumbnailCacheContext.Provider>
      ),
    } satisfies import("@redbamboo/chat").AgentNotePresentation
  }, [activeDiscussion, activeDiscussionId, activeDisplayTitle, grouping.view, handleSelectDiscussion, resolveAgentInfo, resolveFileLink, resolveImageSrc, resolveNoteDiscussion, settings.showDiscussionAvatars, settings.useDiscussionThumbnails, thumbnails])

  const renderEventDetails = useCallback<RenderEventDetails>((event, { part, ...slot }) => {
    if (!activeDiscussion || activeDiscussion.id !== activeDiscussionId || !delegationEventPath(event.key, event.data)) return null
    const sessionId = event.data!.sessionId as string
    const uid = part.messageUid ?? part.messageId
    if (!uid) return null
    let revision: string | null = null
    for (let index = activeMessages.length - 1; index >= 0; index--) {
      const latest = activeMessages[index].parts.findLast(item => {
        if (item.toolName !== "event:delegation" && item.toolName !== "event:delegate") return false
        return parseEventPart(item)?.data?.sessionId === sessionId
      })
      if (!latest) continue
      revision = latest.messageUid ?? latest.messageId ?? activeMessages[index].id
      break
    }
    return (
      <DelegationExchange
        key={activeDiscussion.id + ":" + sessionId}
        {...slot}
        discussion={{
          id: activeDiscussion.id,
          title: activeDisplayTitle ?? "Untitled discussion",
          agent: activeDiscussion.agentId ? resolveAgentInfo(activeDiscussion.agentId)?.name : undefined,
        }}
        sessionId={sessionId}
        opened={{
          uid,
          kind: event.key === "delegate" ? "update" : event.data?.continued === true ? "follow-up" : "brief",
          content: event.text,
          timestamp: event.postedAt ?? event.timestamp,
        }}
        revision={revision}
        navigate={navigate}
      />
    )
  }, [activeDiscussion, activeDiscussionId, activeDisplayTitle, activeMessages, navigate, resolveAgentInfo])

  const { reactions, react, unreact } = useReactions(activeDiscussionId)

  const renderSideActions = useCallback((block: MessageBlock) => {
    const msgKey = block.id
    const msgReactions = reactions[msgKey] ?? []

    const handleToggle = (emoji: string, hasReacted: boolean) => {
      if (hasReacted) unreact(msgKey, emoji)
      else react(msgKey, emoji)
    }
    const handleAdd = (emoji: string) => react(msgKey, emoji)

    return (
      <>
        <ReactionPills reactions={msgReactions} onToggle={handleToggle} />
        <div className="opacity-0 [@media(hover:hover)]:group-hover/msg:opacity-100 group-data-[actions]/msg:opacity-100 transition-opacity duration-150">
          <AddReactionButton onAdd={handleAdd} align={block.role === "user" ? "right" : "left"} />
        </div>
      </>
    )
  }, [reactions, react, unreact])

  // The same picker, as the message menu's React submenu (context menu, ⋮ and touch sheet).
  const reactionEmoji = useReactionEmoji()
  const reactionChoices = useMemo(
    () => reactionEmoji.map(item => ({ emoji: item.emoji, icon: normalizeIcon(item.icon), name: item.name })),
    [reactionEmoji],
  )
  const getMessageActions = useCallback(
    (block: MessageBlock) => reactionMenuEntries(reactionChoices, emoji => react(block.id, emoji)),
    [reactionChoices, react],
  )

  const renderWhatISeeAction = useCallback(({ disabled }: { disabled: boolean }) => {
    if (mobileClient) {
      return (
        <button
          type="button"
          onClick={() => { void captureWhatISee() }}
          disabled={disabled || capturingContext}
          className="w-7 h-7 flex items-center justify-center rounded text-muted-a50 hover:text-text-muted hover:bg-overlay-6 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
          title="Attach what I see"
          aria-label="Attach what I see"
          data-slot="what-i-see-trigger"
          data-mobile-direct-capture="true"
        >
          <i className={`ph-bold ${capturingContext ? "ph-spinner animate-spin" : "ph-eye"} text-xs`} aria-hidden="true" />
        </button>
      )
    }

    return (
      <Popover open={contextPickerOpen} onOpenChange={setContextPickerOpen}>
        <PopoverTrigger
          disabled={disabled || capturingContext}
          className="w-7 h-7 flex items-center justify-center rounded text-muted-a50 hover:text-text-muted hover:bg-overlay-6 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
          title="Attach what I see"
          aria-label="Attach what I see"
          data-slot="what-i-see-trigger"
          data-ui-surface={floating ? "nova:floating-chat" : undefined}
          data-ui-action="capture-context"
        >
          <i className={`ph-bold ${capturingContext ? "ph-spinner animate-spin" : "ph-eye"} text-xs`} aria-hidden="true" />
        </PopoverTrigger>
        <PopoverContent
          side="top"
          align="end"
          className="w-[min(22rem,calc(100vw-1rem))] gap-2 p-3"
          data-slot="what-i-see-source-picker"
        >
          <PopoverHeader>
            <PopoverTitle>Share what I see</PopoverTitle>
            <p className="text-[11px] leading-snug text-text-muted">Choose exactly what Nova can see in this message.</p>
          </PopoverHeader>

          <div className="grid max-h-[22rem] grid-cols-2 gap-2 overflow-y-auto pr-0.5">
            <button
              type="button"
              onClick={() => { void captureWhatISee() }}
              disabled={capturingContext}
              className="group overflow-hidden rounded-lg border border-overlay-10 bg-overlay-4 text-left transition-colors hover:border-accent-a40 hover:bg-overlay-6 disabled:opacity-50"
              data-slot="what-i-see-source-option"
              data-source-kind="redleaf"
            >
              <div className="aspect-video overflow-hidden bg-overlay-8">
                {redLeafPreview?.screenshot ? (
                  <img
                    src={`data:${redLeafPreview.screenshot.mediaType};base64,${redLeafPreview.screenshot.base64}`}
                    alt="Current RedLeaf view"
                    className="h-full w-full object-cover object-top"
                  />
                ) : (
                  <div className="flex h-full items-center justify-center"><i className="ph-bold ph-leaf text-lg text-accent" aria-hidden="true" /></div>
                )}
              </div>
              <div className="px-2 py-1.5">
                <div className="truncate text-xs font-medium text-text-secondary">RedLeaf</div>
                <div className="truncate text-[10px] text-text-muted">Current page only</div>
              </div>
            </button>

            {monitorSources.map(source => (
              <button
                key={source.id}
                type="button"
                onClick={() => { void captureMonitor(source.id) }}
                disabled={capturingContext}
                className="group overflow-hidden rounded-lg border border-overlay-10 bg-overlay-4 text-left transition-colors hover:border-accent-a40 hover:bg-overlay-6 disabled:opacity-50"
                data-slot="what-i-see-source-option"
                data-source-kind="monitor"
                data-source-id={source.id}
              >
                <div className="relative aspect-video overflow-hidden bg-overlay-8">
                  <img
                    src={`data:${source.previewMediaType};base64,${source.previewBase64}`}
                    alt={`Preview of ${source.name}`}
                    className="h-full w-full object-cover"
                  />
                  {source.primary && <span className="absolute right-1 top-1 rounded bg-background/80 px-1 py-0.5 text-[8px] font-medium text-text-secondary">Primary</span>}
                </div>
                <div className="px-2 py-1.5">
                  <div className="truncate text-xs font-medium text-text-secondary">{source.name}</div>
                  <div className="truncate text-[10px] text-text-muted">
                    {source.bounds.width}×{source.bounds.height}
                    {source.applications.length > 0 ? ` · ${source.applications.join(", ")}` : ""}
                  </div>
                </div>
              </button>
            ))}

            {monitorSourcesLoading && monitorSources.length === 0 && (
              <div className="flex aspect-video items-center justify-center rounded-lg border border-overlay-10 bg-overlay-4 text-[10px] text-text-muted" data-slot="what-i-see-sources-loading">
                <i className="ph-bold ph-spinner mr-1.5 animate-spin" aria-hidden="true" /> Loading monitors
              </div>
            )}
          </div>

          {monitorSourcesError && (
            <p className="text-[10px] leading-snug text-text-muted" data-slot="what-i-see-monitors-unavailable">
              Physical monitors are available only in the local RedLeaf app.
            </p>
          )}
        </PopoverContent>
      </Popover>
    )
  }, [captureMonitor, captureWhatISee, capturingContext, contextPickerOpen, floating, mobileClient, monitorSources, monitorSourcesError, monitorSourcesLoading, redLeafPreview])

  const renderWhatISeeAttachment = useCallback(() => activePendingContext ? (
    <PendingContextAttachment
      context={activePendingContext.context}
      onDismiss={() => pendingContext.clear(activeDiscussionId)}
    />
  ) : null, [activeDiscussionId, activePendingContext, pendingContext])

  const { opacity: avatarOpacity } = useAvatarStyle()
  const { showAvatar: avatarEnabled, showDiscussionAvatars, hiddenFriezeMarkers, friezeDensity } = useLocalSettings()
  // Live and every other discussion keep separate marker preferences.
  const markerScope = friezeMarkerScope(activeDiscussion?.type)
  // The store keeps one object until the setting changes, so this stays stable.
  const hiddenMarkers = useMemo(
    () => normalizeHiddenFriezeMarkers(hiddenFriezeMarkers)[markerScope],
    [hiddenFriezeMarkers, markerScope],
  )
  const isFriezeMarkerVisible = useMemo(() => friezeMarkerVisibility(hiddenMarkers), [hiddenMarkers])
  const jobsTransport = useMemo(() => activeDiscussionId
    ? createDiscussionJobsTransport(activeDiscussionId, (path, init) => novaExecution.fetch(path, init))
    : null, [activeDiscussionId])
  const jobsDiscussion = activeDiscussion ? { id: activeDiscussion.id, sessionId: activeDiscussion.sessionId } : null
  const jobs = useChatJobs({
    enabled: !!activeDiscussion,
    conversationKey: activeDiscussionId,
    since: activeMessages[0]?.timestamp,
    transport: jobsTransport,
    mayConcern: data => !!jobsDiscussion && jobFrameConcernsDiscussion(data, jobsDiscussion),
    confidential: activeDiscussion?.confidential ?? false,
    jobHref: computeJobHref,
  })
  const handleStopJob = useCallback(async (job: ChatJob) => {
    if (!activeDiscussionId) return
    try {
      await stopDiscussionJob(activeDiscussionId, job.id, path => api.post(path))
    } catch (error) {
      toast({ title: "Could not stop the job", description: error instanceof Error ? error.message : String(error), variant: "error" })
    }
  }, [activeDiscussionId, toast])
  const { autoFocusMode } = useComposerFocusSettings()
  const showAvatar = !floating && avatarEnabled && !!activeDiscussion
  const showFloatingTabAvatar = floating && avatarEnabled && !!activeDiscussion
  const avatarBase = activeAgent ? activeAgent.avatarUrl : "/api/apps/nova/avatar"
  const avatarSrc = versionedAvatarSrc(avatarBase, avatarRevision)

  const historyPending = historyRenderPending || (isLoadingMessages && activeMessages.length === 0)
  const showDiscussionNotices = !journalOpen && !activeDiscussion?.ownedBy
  const chatArea = activeDiscussion && !discussionSelectionPending ? (
    <div className="flex-1 flex flex-col min-h-0 min-w-0 relative">
      {voiceSession.phase !== "idle" && voiceSession.phase !== "closed" && (
        <VoiceSessionPanel
          agentName={activeAgent?.name ?? "Nova"}
          avatarUrl={avatarSrc}
          phase={voiceSession.phase}
          muted={voiceSession.muted}
          startedAt={voiceSession.startedAt}
          delegating={voiceSession.delegating}
          error={voiceSession.error}
          userCaption={voiceSession.captions.user}
          assistantCaption={voiceSession.captions.assistant}
          captionSpeaker={voiceSession.captions.speaker}
          messages={activeMessages}
          isStreaming={isStreaming}
          resolveImageSrc={resolveImageSrc}
          resolveFileLink={resolveFileLink}
          resolveEventLink={resolveEventLink}
          loadTranscriptPayload={loadTranscriptPayload}
          getTranscriptPayloadMediaUrl={getTranscriptPayloadMediaUrl}
          getTranscriptPayloadDownloadUrl={getTranscriptPayloadDownloadUrl}
          returnFocusRef={voiceSessionTriggerRef}
          onStart={() => void voiceSession.start()}
          onMute={voiceSession.toggleMute}
          onEnd={() => void voiceSession.stop()}
        />
      )}
      {historyPending && (
        <div role="status" className="pointer-events-none absolute inset-0 flex items-center justify-center z-10">
          <i aria-hidden="true" className="ph-bold ph-spinner animate-spin text-2xl opacity-40" />
          <span className="sr-only">Loading discussion.</span>
        </div>
      )}
      {historyLoadFailure && activeMessages.length === 0 && !historyPending && (
        <div className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center px-4">
          <div
            role="alert"
            data-slot="history-load-error"
            className="pointer-events-auto w-full max-w-sm rounded-xl border border-destructive-a30 bg-card px-4 py-3 text-center shadow-lg"
          >
            <i aria-hidden="true" className="ph-bold ph-warning-circle text-xl text-destructive" />
            <p className="mt-1 text-sm font-semibold text-contrast">Couldn&apos;t load this discussion</p>
            <p className="mt-1 break-words text-xs leading-relaxed text-text-muted">{historyLoadFailure.message}</p>
            <button
              type="button"
              onClick={handleRetryHistory}
              className="mt-3 inline-flex h-8 items-center justify-center rounded-md bg-primary px-3 text-xs font-semibold text-primary-foreground transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
            >
              Retry
            </button>
          </div>
        </div>
      )}
      {showDiscussionNotices && (
        <PluginExtensionSlot
          key={activeDiscussion.id}
          targetPluginId="nova"
          slotId="discussion-notices"
          context={{ discussionId: activeDiscussion.id, isStreaming }}
        />
      )}
      <LiveReportHostProvider host={liveReportHost}>
        <ChatBlockRenderersProvider renderers={NOVA_BLOCKS}>
          <ChatPanel
            messages={historyRenderPending ? NO_MESSAGES : activeMessages}
            isStreaming={isStreaming}
            supportsSteering={supportsSteering}
            rollback={conversationRollback}
            backgroundTasks={backgroundTasks}
            onStopBackgroundTask={handleStopBackgroundTask}
            hostActivity={hostRowActivity}
            renderMessageExtra={renderPermissionNotices}
            isReconnecting={!upstreamConnected}
            interrupting={isInterrupting}
            resumePending={isResumePending}
            onSend={handleSend}
            onSendInput={handleSendInput}
            queueTransport={queueTransport}
            queuedEventsWait
            maintenanceStatus={maintenance.status}
            maintenanceStatusUnavailable={maintenance.unavailable}
            hasServerSession={Boolean(payloadSessionId)}
            prepareOutgoingMessage={prepareOutgoingMessage}
            attachmentTransport={attachmentTransport}
            enableFileAttachments
            onInterrupt={handleInterrupt}
            sessionId={activeDiscussionId}
            autoFocusMode={autoFocusMode}
            draftStorageKey={NOVA_DRAFT_STORAGE_KEY}
            disabled={discussionClosed || !!owningApp}
            hideComposer={activeDiscussion.type === "heartbeat" || discussionClosed || !!owningApp}
            pendingQuestion={pendingQuestion}
            questionOutcome={questionOutcome}
            onAnswerQuestion={handleAnswerQuestion}
            permissionApprovals={permissionApprovals}
            onPermissionDecision={owningApp || discussionClosed ? undefined : handlePermissionDecision}
            hasEarlierMessages={hasEarlierMessages && !earlierHistoryLoadFailure}
            onLoadEarlier={handleLoadEarlier}
            isLoadingEarlier={isLoadingEarlier}
            focusMessageRequest={focusRequest}
            onFocusMessageResult={handleFocusMessageResult}
            placeholder={`Talk to ${activeAgent?.name ?? "Nova"}...`}
            header={chatHeader}
            speechBackend={speechBackend}
            pushToTalkKey={pushToTalk.key}
            globalPushToTalk={floating}
            resolveImageSrc={resolveImageSrc}
            resolveFileLink={resolveFileLink}
            resolveEventLink={resolveEventLink}
            loadTranscriptPayload={loadTranscriptPayload}
            getTranscriptPayloadMediaUrl={getTranscriptPayloadMediaUrl}
            getTranscriptPayloadDownloadUrl={getTranscriptPayloadDownloadUrl}
            assistantAvatar={avatarSrc}
            resolveAgentInfo={resolveAgentInfo}
            resolveAgentNote={resolveAgentNote}
            renderEventDetails={renderEventDetails}
            resolveHostEventStatus={resolveHeldEvent}
            isFriezeMarkerVisible={isFriezeMarkerVisible}
            friezeDensity={normalizeFriezeDensity(friezeDensity)}
            hideJobMarkers={hiddenMarkers.includes(JOB_MARKER_KEY)}
            jobs={jobs}
            onStopJob={jobs ? handleStopJob : undefined}
            renderStatusLine={renderStatusLine}
            renderComposerAttachments={renderWhatISeeAttachment}
            renderAttachmentActions={renderWhatISeeAction}
            renderSideActions={renderSideActions}
            getMessageActions={getMessageActions}
            footer={owningApp ? (
              <div
                data-slot="owned-discussion-bar"
                className="shrink-0 border-t border-overlay-8 bg-card px-4 py-3"
              >
                <div className="mx-auto flex max-w-3xl items-center gap-2 text-sm text-text-muted">
                  <i aria-hidden="true" className="ph-bold ph-plugs-connected shrink-0" />
                  <span>This discussion belongs to {owningApp}. Continue it there.</span>
                </div>
              </div>
            ) : discussionClosed ? (
              <div
                data-slot="archived-discussion-bar"
                className="shrink-0 border-t border-overlay-8 bg-card px-4 py-3"
              >
                <div className="mx-auto flex max-w-3xl items-center justify-between gap-3">
                  <span className="text-sm text-text-muted">This discussion is archived</span>
                  <button
                    type="button"
                    onClick={() => { void handleReopen() }}
                    disabled={reopenPending || activeDiscussion.status === "archiving" || !activeDiscussion.sessionId}
                    title={!activeDiscussion.sessionId ? "This archive has no provider session to reopen" : undefined}
                    className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-semibold text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <i aria-hidden="true" className={`ph-bold ${reopenPending ? "ph-spinner animate-spin" : "ph-arrow-counter-clockwise"}`} />
                    {activeDiscussion.status === "archiving" ? "Finalizing…" : reopenPending ? "Reopening…" : "Reopen"}
                  </button>
                </div>
              </div>
            ) : null}
          />
        </ChatBlockRenderersProvider>
      </LiveReportHostProvider>
    </div>
  ) : (
    <div
      data-slot={discussionSelectionPending || isResolvingDiscussion ? "chat-panel" : undefined}
      className="flex flex-1 min-h-0 min-w-0 items-center justify-center text-text-muted"
    >
      {isSpawning || discussionSelectionPending || isResolvingDiscussion || discussionListLoadState === "loading" ? (
        <div role="status" className="text-center">
          <i aria-hidden="true" className="ph-bold ph-spinner animate-spin text-2xl mx-auto mb-3 opacity-40" />
          <p className="text-sm">{isSpawning ? "Starting discussion…" : discussionSelectionPending || isResolvingDiscussion ? "Loading discussion…" : "Loading discussions…"}</p>
        </div>
      ) : (
        <div className="text-center">
          <i className="ph-bold ph-star text-3xl mx-auto mb-3 opacity-30" />
          <p className="text-sm mb-4">Start a conversation</p>
          <button
            onClick={openNewDiscussion}
            className="text-xs px-3 py-1.5 rounded bg-overlay-10 hover:bg-overlay-15 text-contrast transition-colors"
          >
            <i className="ph-bold ph-plus mr-1.5" />
            New Discussion
          </button>
        </div>
      )}
    </div>
  )

  return (
    <div
      className="relative h-full w-full"
      data-slot={floating ? "floating-surface-root" : undefined}
      data-ui-surface={floating ? "nova:floating-chat" : undefined}
      data-surface-state={floating ? "open" : undefined}
      data-view={floating ? (mobileTab === 0 ? "discussions" : "chat") : undefined}
      data-discussion-id={floating ? (activeDiscussionId ?? undefined) : undefined}
      data-streaming={floating ? (isStreaming || undefined) : undefined}
      data-ui-shortcuts={floating ? FLOATING_NOVA_SHORTCUT_LIST : undefined}
    >
      {!floating && activeDiscussion && (
        <DiscussionShortcuts
          discussionId={activeDiscussion.id}
          voiceAvailable={voiceAvailable}
          canManage={canManageDiscussion}
          canShare={!discussionClosed && !owningApp && !activeDiscussion.confidential}
          isLive={activeDiscussion.type === "live"}
          openVoice={openVoice}
          openSessionInfo={openSessionInfo}
          toggleArtifacts={toggleArtifacts}
          startRename={() => setTitleEditing(true)}
          updateTitle={handleUpdateTitle}
          share={handleShare}
          rotate={handleRotateDiscussion}
        />
      )}
      {showAvatar && (
        <div
          data-avatar-bounce-frame
          className="absolute top-4 left-4 w-[80px] h-[80px] z-20 rounded-full overflow-hidden md:hidden p-1.5"
          style={{ backgroundColor: "var(--background)" }}
        >
          <TransitioningAgentAvatar
            src={avatarSrc}
            agentId={activeDiscussion?.agentId ?? null}
            discussionId={activeDiscussionId}
            variant="mobile"
            imageOpacity={avatarOpacity}
          />
        </div>
      )}
      {showFloatingTabAvatar && (
        <div
          role="presentation"
          aria-hidden="true"
          data-slot="floating-surface-avatar"
          data-avatar-bounce-frame
          className="pointer-events-none absolute top-4 left-1/2 -translate-x-1/2 w-[80px] h-[80px] z-30 rounded-full overflow-hidden p-1.5"
          style={{ backgroundColor: "var(--background)" }}
        >
          <TransitioningAgentAvatar
            src={avatarSrc}
            agentId={activeDiscussion?.agentId ?? null}
            discussionId={activeDiscussionId}
            variant="mobile"
            imageOpacity={avatarOpacity}
          />
        </div>
      )}
      <MasterDetailLayout
        layoutKey={floating ? undefined : "nova-discussions"}
        presentation={floating ? "compact" : "responsive"}
        className={!floating ? `nova-chat-layout${showAvatar ? " nova-chat-layout--mobile-avatar" : ""}` : undefined}
        mobileLabels={["Discussions", "Chat"]}
        mobileTab={mobileTab}
        onMobileTabChange={setMobileTab}
        sidebar={
        <>
          {sidebarHeader}
          <div className="flex-1 overflow-hidden" onMouseDownCapture={keepComposerFocus}>
            <DiscussionThumbnailCacheContext.Provider value={thumbnails}>
              <LiveQuotaContext.Provider value={liveQuota}>
                <DiscussionSidebar
                  discussions={archiveMode ? filteredArchivedDiscussions : filteredDiscussions}
                  loadState={archiveMode ? archiveListLoadState : discussionListLoadState}
                  onRetryLoad={handleRetryDiscussionList}
                  delegationActivity={delegationActivity}
                  backgroundTasks={backgroundTasksByDiscussion}
                  awaitingAnswer={awaitingAnswerByDiscussion}
                  activeDiscussionId={synchronizedDiscussionId ?? activeDiscussionId}
                  onSelect={handleSelectDiscussion}
                  onArchive={handleCloseDiscussion}
                  onRotate={handleRotateDiscussion}
                  onDismiss={dismissDiscussion}
                  onRowAction={handleRowAction}
                  onUpdateTitle={handleUpdateTitle}
                  onFloat={floatingSurface?.supported && !floating ? handleFloatDiscussion : undefined}
                  onStop={stopDiscussion}
                  getAgent={getAgent}
                  heartbeatAgents={heartbeatAgents}
                  showAvatars={showDiscussionAvatars}
                  grouping={grouping.view}
                  groupingEnabled={!!settings.groupDiscussions}
                  refineTitles={settings.refineDiscussionTitles}
                  useThumbnails={settings.useDiscussionThumbnails}
                  groupingLoading={grouping.loading}
                  groupingUnavailable={grouping.unavailable}
                  onRetryGrouping={grouping.retry}
                  collapsedGroups={collapsedDiscussionGroups}
                  onToggleGroup={toggleDiscussionGroup}
                  groupSort={groupSort}
                  onReorderGroups={reorderDiscussionGroups}
                  archiveMode={archiveMode}
                  archiveQuery={archiveQuery}
                  archiveHasMore={archiveHasMore}
                  onArchiveQueryChange={setArchiveQuery}
                  onExitArchiveMode={handleExitArchiveMode}
                  onLoadMoreArchived={handleLoadMoreArchived}
                />
              </LiveQuotaContext.Provider>
            </DiscussionThumbnailCacheContext.Provider>
          </div>
          {showAvatar && (
            <div
              className="flex justify-center px-3 pb-5 pt-2 w-full"
              style={{ opacity: avatarOpacity }}
            >
              <div data-avatar-bounce-frame className="relative w-full max-w-[256px] aspect-square rounded-full overflow-hidden" style={{ backgroundColor: "var(--background)" }}>
                <TransitioningAgentAvatar
                  src={avatarSrc}
                  agentId={activeDiscussion?.agentId ?? null}
                  discussionId={activeDiscussionId}
                  variant="sidebar"
                />
              </div>
            </div>
          )}
        </>
      }
      detail={
        // The chat stays mounted and in its place under the Journal, so its scroll anchor and its
        // draft are as it left them. `invisible` keeps its layout, unlike taking it off the page.
        <div className="relative flex min-h-0 min-w-0 flex-1 flex-col">
          <div
            ref={discussionPaneRef}
            data-slot="nova-discussion-pane"
            className={`relative flex min-h-0 min-w-0 flex-1 flex-col${journalOpen ? " invisible" : ""}`}
          >
            {chatArea}
            <div ref={transcriptFadeRef} inert aria-hidden="true" className="pointer-events-none absolute inset-0 z-10 overflow-hidden" />
          </div>
          {journalOpen && (
            <div data-slot="journal-surface" className="absolute inset-0 z-20 flex min-h-0 flex-col">
              <PanelHeader
                leading={liveHeartbeatTabs
                  ? <div className="flex flex-1 min-w-0 self-stretch">{liveHeartbeatTabs}</div>
                  : <span className="truncate text-sm font-medium text-contrast">Journal</span>}
              />
              <JournalView agent={journalAgent} date={journalDate ?? null} resolveImageSrc={resolveImageSrc} />
            </div>
          )}
        </div>
      }
    />
    <ArtifactsSurface
      discussionId={activeDiscussionId}
      open={artifactsOpen && !!activeDiscussion && !journalOpen}
      onClose={closeArtifacts}
      isStreaming={isStreaming}
      resolveImageSrc={resolveImageSrc}
      resolveFileLink={resolveFileLink}
      onJumpToMessage={jumpToMessage}
    />
    {seekingEarlier && (
      <div
        role="status"
        data-slot="go-to-message-status"
        className="pointer-events-none absolute left-1/2 top-16 z-30 flex -translate-x-1/2 items-center gap-2 rounded-full border border-overlay-10 bg-popover px-3 py-1.5 text-xs text-text-muted shadow-md"
      >
        <i aria-hidden="true" className="ph-bold ph-spinner animate-spin" />
        Loading earlier messages
      </div>
    )}
    <ShareDialog
      open={shareDialogOpen}
      onOpenChange={setShareDialogOpen}
      shareUrl={share.shareUrl}
      loading={share.loading}
      error={share.error}
    />
    </div>
  )
}
