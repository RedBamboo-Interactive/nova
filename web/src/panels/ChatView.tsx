import { useState, useCallback, useEffect, useLayoutEffect, useMemo, useRef, type ButtonHTMLAttributes, type Ref } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger, MasterDetailLayout, PanelHeader, Popover, PopoverContent, PopoverHeader, PopoverTitle, PopoverTrigger, Switch, Tabs, TabsList, TabsTrigger, useToast, useUiEnvironment } from "@redbamboo/ui"
import { ChatPanel, PendingContextAttachment, SessionInfoButton, ShareDialog, fetchTranscriptPayload, useComposerFocusSettings, usePushToTalkSettings, type AttachmentTransport, type ChatInputPart, type ChatQueueSnapshot, type ChatQueueTransport, type ChatQueuedItem, type ImageAttachment, type OutgoingMessageDraft, type SendOptions, type MessageBlock, type ParsedEvent, type ProviderUsageSnapshot, type QuestionAnswerPayload, type TranscriptPayloadLoader, type TranscriptPayloadRef, type UploadedAttachment } from "@redbamboo/chat"
import { captureVisibleAppContext, useBreadcrumbLabel, formatContextMessage, getEntityHref, runUiSurfaceAction, useCommand, useUiSurface, VisibleAppContextCaptureError, type UiSurfaceActionResult, type VisibleAppContext } from "@redbamboo/utility"
import { DiscussionSidebar, type DiscussionRowAction } from "../components/discussion/discussion-sidebar"
import { isMobileClient } from "../components/floating-nova-support"
import { EditableTitle } from "../components/discussion/editable-title"
import { DISCUSSION_SHORTCUTS, DiscussionActionItems } from "../components/discussion/discussion-actions-menu"
import { AgentPicker } from "../components/agent-picker"
import { TransitioningAgentAvatar } from "../components/transitioning-agent-avatar"
import { NovaStatusLine } from "../components/nova-status-line"
import { DelegationsInfo } from "../components/delegation-activity"
import { ReactionPills, AddReactionButton } from "../components/discussion/reactions"
import { createNovaSpeechBackend } from "../lib/speech"
import { useLocalSettings } from "../hooks/use-local-settings"
import { useAgents } from "../hooks/use-agents"
import { useReactions } from "../hooks/use-reactions"
import { useDisc, useNovaPendingContext } from "../App"
import { useSessionStats } from "../hooks/use-session-stats"
import { useMaintenanceStatus } from "../hooks/use-maintenance-status"
import { useShare } from "../hooks/use-share"
import { setSettings } from "../lib/settings-store"
import { api, novaExecution } from "../lib/api"
import { findLiveHeartbeatPair } from "../lib/live-heartbeat"
import { getAdjacentSidebarDiscussion, getSidebarDiscussionOrder } from "../lib/discussion-navigation"
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
import { isDiscussionSelectionCurrent, resolveRequestedDiscussionId } from "../lib/discussion-view-selection"
import { captureMonitorVisualSource, listMonitorVisualSources, monitorCaptureToContext, type MonitorVisualSource } from "../lib/visual-capture"
import { delegationSessionPath } from "../lib/delegation-session-link"
import { useVoiceSession } from "../hooks/use-voice-session"
import { VoiceSessionPanel } from "../components/voice-session-panel"

const speechBackend = createNovaSpeechBackend()

interface QualityTierInfo { slug: string; label: string; color?: string; icon?: string }
interface ProviderInfo {
  slug: string
  name: string
  backend: string
  icon?: string
  iconSvgPath?: string
  color?: string
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

export interface ChatViewProps {
  presentation?: "standard" | "floating"
  selectedDiscussionId?: string | null
  onSelectDiscussion?: (id: string) => void
  onNewDiscussion?: () => void
  onDock?: () => void
}

export function ChatView({
  presentation = "standard",
  selectedDiscussionId = null,
  onSelectDiscussion,
  onNewDiscussion,
  onDock,
}: ChatViewProps = {}) {
  const { discussionId: urlDiscussionId } = useParams()
  const navigate = useNavigate()
  const disc = useDisc()
  const environment = useUiEnvironment()
  const floating = presentation === "floating"
  const mobileClient = isMobileClient(environment.window.navigator)
  const requestedDiscussionId = floating ? selectedDiscussionId : (urlDiscussionId ?? null)
  const [pendingDiscussionId, setPendingDiscussionId] = useState<string | null>(null)
  const [mobileTab, setMobileTab] = useState(0)
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
    activeDiscussion,
    activeDiscussionId,
    activeMessages,
    isStreaming,
    isInterrupting,
    isResumePending,
    isSpawning,
    pendingQuestion,
    questionOutcome,
    selectDiscussion,
    clearDiscussionSelection,
    sendMessage,
    sendInput,
    interruptDiscussion,
    answerQuestion,
    archiveDiscussion,
    rotateDiscussion,
    dismissDiscussion,
    renameDiscussion,
    updateDiscussionTitle,
    setConfidential,
    resumeDiscussion,
    loadEarlierMessages,
    retryHistoryLoad,
    isLoadingMessages,
    hasEarlierMessages,
    isLoadingEarlier,
    historyLoadFailure,
    earlierHistoryLoadFailure,
    upstreamConnected,
    delegationActivity,
    refreshDelegationActivity,
    syncAndRefresh,
  } = disc

  const defaultDiscussionId = getSidebarDiscussionOrder(discussions, null)[0]?.id ?? null
  const synchronizedDiscussionId = resolveRequestedDiscussionId(
    requestedDiscussionId,
    pendingDiscussionId,
    defaultDiscussionId,
  )

  const maintenance = useMaintenanceStatus()
  const voiceSession = useVoiceSession(activeDiscussionId)
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

  useBreadcrumbLabel(
    !floating && urlDiscussionId ? `/apps/nova/chat/${urlDiscussionId}` : undefined,
    activeDiscussion?.type === "live"
      ? "Live"
      : activeDiscussion?.type === "heartbeat"
        ? "Heartbeat"
        : activeDiscussion?.title || "New Discussion",
  )

  const { agents, getAgent, refreshAgents } = useAgents()
  const multiAgent = agents.length > 1
  const settings = useLocalSettings()
  const pushToTalk = usePushToTalkSettings()
  const agentFilter = settings.agentFilter

  const filteredDiscussions = getSidebarDiscussionOrder(discussions, agentFilter)

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

  const sessionStats = useSessionStats(activeDiscussion?.sessionId, isStreaming, activeDiscussion)
  const loadProviderUsage = useCallback((provider: string, forceRefresh = false) =>
    api.get<ProviderUsageSnapshot>(
      `/ai-session/providers/${encodeURIComponent(provider)}/usage${forceRefresh ? "?refresh=true" : ""}`,
    ), [])
  const share = useShare(activeDiscussion?.entityId)
  const [shareDialogOpen, setShareDialogOpen] = useState(false)
  const [sessionInfoOpen, setSessionInfoOpen] = useState(false)
  const [titleEditing, setTitleEditing] = useState(false)
  const [titleUpdatePending, setTitleUpdatePending] = useState(false)
  const [confidentialityPending, setConfidentialityPending] = useState(false)
  const [qualityTiers, setQualityTiers] = useState<QualityTierInfo[]>([])
  const [providers, setProviders] = useState<ProviderInfo[]>([])
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
    setTitleUpdatePending(false)
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
    if (activeDiscussion.type === "heartbeat" || activeDiscussion.status === "archived" || activeDiscussion.status === "stopped") {
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
    if (activeDiscussion.type === "heartbeat" || activeDiscussion.status === "archived" || activeDiscussion.status === "stopped") return

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

  const handleSendInput = useCallback((input: ChatInputPart[], attachments: UploadedAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    return sendInput(activeDiscussionId, input, attachments, options)
  }, [activeDiscussionId, sendInput])

  const queueTransport = useMemo<ChatQueueTransport | undefined>(() => activeDiscussionId ? ({
    list: () => api.get<ChatQueueSnapshot>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue?includeTerminal=true`),
    cancel: (itemId) => api.delete<ChatQueuedItem>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/${itemId}`),
    retry: (itemId) => api.post<ChatQueuedItem>(`/api/apps/nova/discussions/${activeDiscussionId}/input-queue/${itemId}/retry`),
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

  const handleResume = useCallback(async () => {
    if (!activeDiscussionId) return
    await resumeDiscussion(activeDiscussionId)
  }, [activeDiscussionId, resumeDiscussion])

  const handleLoadEarlier = useCallback(() => {
    if (!activeDiscussionId) return
    return loadEarlierMessages(activeDiscussionId)
  }, [activeDiscussionId, loadEarlierMessages])

  const handleRetryHistory = useCallback(() => {
    if (!activeDiscussionId) return
    return retryHistoryLoad(activeDiscussionId)
  }, [activeDiscussionId, retryHistoryLoad])

  const handleSelectDiscussion = useCallback((id: string) => {
    setPendingDiscussionId(id)
    if (floating) onSelectDiscussion?.(id)
    else navigate(`/apps/nova/chat/${id}`)
    // The route/host selection is the authority. Selecting the discussion
    // locally here as well can race the still-old requested id and bounce the
    // store old -> new -> old -> new. The layout effect applies the requested
    // selection before paint while the render guard hides any stale transcript.
    setMobileTab(1)
  }, [floating, navigate, onSelectDiscussion])

  const handleRotateDiscussion = useCallback(async (id: string) => {
    const followReplacement = activeDiscussionId === id
    const newDiscussionId = await rotateDiscussion(id)
    if (followReplacement && newDiscussionId) handleSelectDiscussion(newDiscussionId)
  }, [activeDiscussionId, handleSelectDiscussion, rotateDiscussion])

  // Semantic events navigate only this router/client. Never round-trip through
  // the global codered.navigate WebSocket event for delegation markers.
  const resolveEventLink = useCallback((event: ParsedEvent) => {
    if (event.key === "discussion") {
      const discussionId = event.data?.discussionId
      if (typeof discussionId !== "string" || !discussionId) return undefined
      return () => handleSelectDiscussion(discussionId)
    }
    if (event.key === "delegation") {
      const path = delegationSessionPath(event.data)
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
      const adjacent = getAdjacentSidebarDiscussion(discussions, activeDiscussionId, direction, agentFilter)
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
  }, [activeDiscussionId, agentFilter, discussions, environment.document, floating, handleSelectDiscussion, openNewDiscussion])

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

  const handleShare = useCallback(() => {
    share.reset()
    share.createShare()
    setShareDialogOpen(true)
  }, [share])

  const handleRetryDiscussionList = useCallback(() => {
    syncAndRefresh().catch(() => {})
  }, [syncAndRefresh])

  const handleUpdateTitle = useCallback(async (id: string) => {
    if (titleUpdatePending) return
    setTitleUpdatePending(true)
    try {
      const updated = await updateDiscussionTitle(id)
      toast({ variant: "success", title: "Title updated", description: updated.title || "New discussion" })
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not update title",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    } finally {
      setTitleUpdatePending(false)
    }
  }, [titleUpdatePending, toast, updateDiscussionTitle])

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
  const voiceAvailable = !!activeDiscussion && activeDiscussion.status !== "archived" && activeDiscussion.status !== "stopped"
    && voiceSession.phase !== "starting" && voiceSession.phase !== "connected"
  const startVoice = useCallback(() => { void voiceSession.start() }, [voiceSession])
  const openSessionInfo = useCallback(() => {
    void refreshDelegationActivity(true)
    setSessionInfoOpen(true)
  }, [refreshDelegationActivity])

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
    else if (pendingRowAction.action === "rename") setTitleEditing(true)
    else if (pendingRowAction.action === "voice") startVoice()
    else handleShare()
  }, [activeDiscussion?.id, handleShare, openSessionInfo, pendingRowAction, startVoice])

  // Desktop shortcuts for the open discussion; the menus show the same keys.
  const canManageDiscussion = activeDiscussion?.type === "chat"
  const docked = !floating && !!activeDiscussion
  useCommand("nova-discussion-voice", {
    label: "Start Voice",
    description: "Start a voice session in the open discussion",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.voice,
    enabled: docked && voiceAvailable,
    action: startVoice,
  })

  useCommand("nova-discussion-info", {
    label: "Discussion Info",
    description: "Show the session, model and delegations of the open discussion",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.info,
    enabled: docked,
    action: openSessionInfo,
  })

  useCommand("nova-discussion-rename", {
    label: "Rename Discussion",
    description: "Edit the title of the open discussion",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.rename,
    enabled: docked && canManageDiscussion,
    action: () => setTitleEditing(true),
  })

  useCommand("nova-discussion-update-title", {
    label: "Update Discussion Title",
    description: "Regenerate the title of the open discussion from its conversation",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.updateTitle,
    enabled: docked && canManageDiscussion,
    action: () => { if (activeDiscussionId) void handleUpdateTitle(activeDiscussionId) },
  })

  useCommand("nova-discussion-share", {
    label: "Share Discussion",
    description: "Create a share link for the open discussion",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.share,
    enabled: docked && !activeDiscussion?.confidential,
    action: handleShare,
  })

  useCommand("nova-reset-live-timeline", {
    label: "Reset Live Timeline",
    description: "Rotate Live onto a fresh timeline",
    group: "Discussions",
    shortcut: DISCUSSION_SHORTCUTS.reset,
    keywords: ["rotate", "reset", "live"],
    enabled: docked && activeDiscussion?.type === "live",
    action: () => { if (activeDiscussionId) void handleRotateDiscussion(activeDiscussionId) },
  })

  const liveHeartbeatPair = findLiveHeartbeatPair(discussions, activeDiscussion)

  const liveHeartbeatTabs = liveHeartbeatPair && (
    <Tabs
      value={activeDiscussion?.type === "heartbeat" ? "heartbeat" : "live"}
      onValueChange={(value) => handleSelectDiscussion(
        value === "heartbeat" ? liveHeartbeatPair.heartbeat.id : liveHeartbeatPair.live.id,
      )}
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
          className="after:rounded-full after:bg-primary group-data-[orientation=horizontal]/tabs:after:inset-x-1 group-data-[orientation=horizontal]/tabs:after:bottom-[-2px]"
          style={{ height: "100%" }}
        >
          <i
            aria-hidden="true"
            className={`ph-bold ph-broadcast text-[12px] ${liveHeartbeatPair.live.status === "thinking" ? "animate-pulse" : ""}`}
            style={{ color: "var(--color-status-live)" }}
          />
          Live
        </TabsTrigger>
        <TabsTrigger
          value="heartbeat"
          aria-label={liveHeartbeatPair.heartbeat.status === "thinking" ? "Heartbeat, tick running" : "Heartbeat"}
          className="after:rounded-full after:bg-primary group-data-[orientation=horizontal]/tabs:after:inset-x-1 group-data-[orientation=horizontal]/tabs:after:bottom-[-2px]"
          style={{ height: "100%" }}
        >
          <i
            aria-hidden="true"
            className={`ph-bold ph-heartbeat text-[12px] ${liveHeartbeatPair.heartbeat.status === "thinking" ? "animate-pulse" : ""}`}
            style={{ color: "var(--color-accent-gold)" }}
          />
          Heartbeat
        </TabsTrigger>
      </TabsList>
    </Tabs>
  )

  const activeDelegations = activeDiscussion ? delegationActivity[activeDiscussion.id] : undefined

  const sessionInfoContent = activeDiscussion && (
    <div className="space-y-3">
      <div className="flex items-start justify-between gap-3 rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2.5">
        <div className="flex min-w-0 items-start gap-2.5">
          <i className="ph-bold ph-lock-simple mt-0.5 text-sm text-text-muted" aria-hidden="true" />
          <div className="min-w-0">
            <div className="text-xs font-medium text-contrast">Confidential</div>
            <p className="mt-0.5 text-[11px] leading-relaxed text-text-muted">
              Excludes this discussion from Live activity, heartbeat and other discussions&apos; context, and bulk exports. The share control is hidden. It remains stored and accessible here.
            </p>
          </div>
        </div>
        <Switch
          size="sm"
          className="mt-0.5 shrink-0"
          checked={activeDiscussion.confidential ?? false}
          onCheckedChange={handleConfidentialToggle}
          disabled={confidentialityPending}
          aria-busy={confidentialityPending}
          aria-label="Confidential discussion"
        />
      </div>
      <DelegationsInfo activity={activeDelegations} />
    </div>
  )

  const chatHeader = activeDiscussion && (
    <>
    <PanelHeader
      leading={
        liveHeartbeatTabs ? <div className="flex flex-1 min-w-0 self-stretch">{liveHeartbeatTabs}</div> : (
          <EditableTitle
            title={activeDiscussion.title || "New discussion"}
            onRename={(title) => renameDiscussion(activeDiscussion.id, title)}
            editing={titleEditing}
            onEditingChange={setTitleEditing}
          />
        )
      }
    >
      {!floating && (
        <>
          {/* The controlled modal portals outside this hidden legacy trigger. */}
          <div className="hidden">
            <SessionInfoButton
              stats={sessionStats}
              messages={activeMessages}
              agent={activeAgent ? {
                id: activeAgent.id,
                name: activeAgent.name,
                avatarUrl: activeAgent.avatarUrl,
                href: getEntityHref("agent", activeAgent.id),
              } : null}
              qualityTierOptions={qualityTiers.map(t => ({ value: t.slug, label: t.label, color: t.color, icon: t.icon }))}
              providerOptions={providers.map(p => ({
                value: p.slug,
                aliases: [p.backend],
                label: p.name,
                color: p.color,
                icon: p.icon,
                iconSvgPath: p.iconSvgPath,
              }))}
              loadProviderUsage={loadProviderUsage}
              open={sessionInfoOpen}
              onOpenChange={setSessionInfoOpen}
            >
              {sessionInfoContent}
            </SessionInfoButton>
          </div>
          <DropdownMenu>
            <DropdownMenuTrigger
              ref={voiceSessionTriggerRef}
              className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
              aria-label="Conversation actions"
              title="Conversation actions"
            >
              <i aria-hidden="true" className="ph-bold ph-dots-three-vertical text-base" />
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" sideOffset={4}>
              <DiscussionActionItems
                showShortcuts
                onVoice={startVoice}
                voiceDisabled={!voiceAvailable}
                onInfo={openSessionInfo}
                onRename={canManageDiscussion ? () => setTitleEditing(true) : undefined}
                onUpdateTitle={canManageDiscussion ? () => void handleUpdateTitle(activeDiscussion.id) : undefined}
                updateTitlePending={titleUpdatePending}
                onShare={!activeDiscussion.confidential ? handleShare : undefined}
                onFloat={floatingSurface?.supported ? () => handleFloatDiscussion(activeDiscussion.id) : undefined}
                onReset={activeDiscussion.type === "live" ? () => void handleRotateDiscussion(activeDiscussion.id) : undefined}
                onClose={canManageDiscussion ? () => handleCloseDiscussion(activeDiscussion.id) : undefined}
              />
            </DropdownMenuContent>
          </DropdownMenu>
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
        <SessionInfoButton
          stats={sessionStats}
          messages={activeMessages}
          agent={activeAgent ? {
            id: activeAgent.id,
            name: activeAgent.name,
            avatarUrl: activeAgent.avatarUrl,
            href: getEntityHref("agent", activeAgent.id),
          } : null}
          qualityTierOptions={qualityTiers.map(t => ({ value: t.slug, label: t.label, color: t.color, icon: t.icon }))}
          providerOptions={providers.map(p => ({
            value: p.slug,
            aliases: [p.backend],
            label: p.name,
            color: p.color,
            icon: p.icon,
            iconSvgPath: p.iconSvgPath,
          }))}
          loadProviderUsage={loadProviderUsage}
        >
          {sessionInfoContent}
        </SessionInfoButton>
      )}
    </PanelHeader>
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

  const sidebarHeader = (
    <PanelHeader title="Discussions">
      {multiAgent && (
        <AgentPicker
          agents={agents}
          selectedId={agentFilter}
          onSelect={(id) => setSettings({ agentFilter: id })}
          showAll
        />
      )}
      <button
        onClick={openNewDiscussion}
        className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10"
        title={`New discussion (${floating ? "Alt+N" : "Ctrl+N"})`}
      >
        <i className="ph-bold ph-plus text-xs" />
        <span>New</span>
      </button>
    </PanelHeader>
  )

  const renderStatusLine = useCallback(({ isStreaming, isReconnecting, messages }: { isStreaming: boolean; isReconnecting: boolean; messages: import("@redbamboo/chat").MessageBlock[] }) => (
    <NovaStatusLine isStreaming={isStreaming} isReconnecting={isReconnecting} messages={messages} delegationActivity={activeDelegations} />
  ), [activeDelegations])

  const resolveAgentInfo = useCallback((agentId: string) => {
    const agent = getAgent(agentId)
    if (!agent) return undefined
    return { name: agent.name, avatarUrl: agent.avatarUrl }
  }, [getAgent])

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
  const { showAvatar: avatarEnabled } = useLocalSettings()
  const { autoFocusMode } = useComposerFocusSettings()
  const showAvatar = !floating && avatarEnabled && !!activeDiscussion
  const showFloatingTabAvatar = floating && avatarEnabled && !!activeDiscussion
  const [avatarVersion, setAvatarVersion] = useState(0)
  useEffect(() => {
    const handler = () => setAvatarVersion(v => v + 1)
    window.addEventListener("nova:avatar-changed", handler)
    return () => window.removeEventListener("nova:avatar-changed", handler)
  }, [])
  const avatarBase = activeAgent ? activeAgent.avatarUrl : "/api/apps/nova/avatar"
  const avatarSrc = avatarVersion
    ? `${avatarBase}${avatarBase.includes("?") ? "&" : "?"}v=${avatarVersion}`
    : avatarBase

  const chatArea = activeDiscussion && isDiscussionSelectionCurrent(synchronizedDiscussionId, activeDiscussionId) ? (
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
          onMute={voiceSession.toggleMute}
          onEnd={() => void voiceSession.stop()}
        />
      )}
      {isLoadingMessages && activeMessages.length === 0 && (
        <div className="absolute inset-0 flex items-center justify-center z-10">
          <i className="ph-bold ph-spinner animate-spin text-2xl opacity-40" />
        </div>
      )}
      {historyLoadFailure && activeMessages.length === 0 && !isLoadingMessages && (
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
      <ChatPanel
        messages={activeMessages}
        isStreaming={isStreaming}
        isReconnecting={!upstreamConnected}
        interrupting={isInterrupting}
        resumePending={isResumePending}
        onSend={handleSend}
        onSendInput={handleSendInput}
        queueTransport={queueTransport}
        maintenanceStatus={maintenance.status}
        maintenanceStatusUnavailable={maintenance.unavailable}
        hasServerSession={Boolean(payloadSessionId)}
        prepareOutgoingMessage={prepareOutgoingMessage}
        attachmentTransport={attachmentTransport}
        enableFileAttachments
        onInterrupt={handleInterrupt}
        sessionId={activeDiscussionId}
        autoFocusMode={autoFocusMode}
        draftStorageKey="nova-drafts"
        disabled={activeDiscussion.status === "archived" || activeDiscussion.status === "stopped"}
        hideComposer={activeDiscussion.type === "heartbeat"}
        pendingQuestion={pendingQuestion}
        questionOutcome={questionOutcome}
        onAnswerQuestion={handleAnswerQuestion}
        onResume={activeDiscussion.status === "stopped" ? handleResume : undefined}
        hasEarlierMessages={hasEarlierMessages && !earlierHistoryLoadFailure}
        onLoadEarlier={handleLoadEarlier}
        isLoadingEarlier={isLoadingEarlier}
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
        renderStatusLine={renderStatusLine}
        renderComposerAttachments={renderWhatISeeAttachment}
        renderAttachmentActions={renderWhatISeeAction}
        renderSideActions={renderSideActions}
      />
    </div>
  ) : (
    <div className="flex-1 flex items-center justify-center text-text-muted">
      {isSpawning || discussionListLoadState === "loading" ? (
        <div className="text-center">
          <i className="ph-bold ph-spinner animate-spin text-2xl mx-auto mb-3 opacity-40" />
          <p className="text-sm">{isSpawning ? "Starting discussion…" : "Loading discussions…"}</p>
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
          <div className="flex-1 overflow-hidden">
            <DiscussionSidebar
              discussions={filteredDiscussions}
              loadState={discussionListLoadState}
              onRetryLoad={handleRetryDiscussionList}
              delegationActivity={delegationActivity}
              activeDiscussionId={synchronizedDiscussionId ?? activeDiscussionId}
              onSelect={handleSelectDiscussion}
              onArchive={handleCloseDiscussion}
              onRotate={handleRotateDiscussion}
              onDismiss={dismissDiscussion}
              onRowAction={handleRowAction}
              onUpdateTitle={handleUpdateTitle}
              onFloat={floatingSurface?.supported && !floating ? handleFloatDiscussion : undefined}
              getAgent={getAgent}
              multiAgent={multiAgent}
            />
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
      detail={chatArea}
    />
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
