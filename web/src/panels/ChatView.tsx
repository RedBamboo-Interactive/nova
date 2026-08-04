import { useState, useCallback, useEffect, useMemo } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { MasterDetailLayout, PanelHeader, Switch, Tabs, TabsList, TabsTrigger } from "@redbamboo/ui"
import { ChatPanel, ContextIndicator, ShareDialog, fetchTranscriptPayload, type AttachmentTransport, type ChatInputPart, type ImageAttachment, type SendOptions, type MessageBlock, type ParsedEvent, type QuestionAnswerPayload, type TranscriptPayloadLoader, type TranscriptPayloadRef, type UploadedAttachment } from "@redbamboo/chat"
import { PluginExtensionSlot, useBreadcrumbLabel, formatContextMessage } from "@redbamboo/utility"
import { DiscussionSidebar } from "../components/discussion/discussion-sidebar"
import { EditableTitle } from "../components/discussion/editable-title"
import { AgentPicker } from "../components/agent-picker"
import { NovaStatusLine } from "../components/nova-status-line"
import { ReactionPills, AddReactionButton } from "../components/discussion/reactions"
import { createNovaSpeechBackend } from "../lib/speech"
import { useLocalSettings } from "../hooks/use-local-settings"
import { useAgents } from "../hooks/use-agents"
import { useReactions } from "../hooks/use-reactions"
import { useDisc, useNovaPendingContext } from "../App"
import { useSessionStats } from "../hooks/use-session-stats"
import { useShare } from "../hooks/use-share"
import { setSettings } from "../lib/settings-store"
import { api } from "../lib/api"
import { findLiveHeartbeatPair } from "../lib/live-heartbeat"
import { getSidebarDiscussionOrder } from "../lib/discussion-navigation"

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

function resolveFileLink(filePath: string, opts?: { line?: number }): (() => void) | undefined {
  const norm = filePath.replace(/\\/g, "/")
  // Expect absolute paths like T:/Projects/repoName/some/file.ts
  const match = norm.match(/^([A-Za-z]:\/[^/]+\/[^/]+)\/(.+)$/)
  if (!match) return undefined
  const project = match[1]!
  const relPath = match[2]!
  const line = opts?.line ? `?line=${opts.line}` : ""
  const codePath = `/code/${encodeURIComponent(project)}/${encodeURIComponent(relPath)}${line}`
  return () => navigateCodeRed(codePath)
}

function navigateCodeRed(path: string) {
  // CodeRed is a plugin on this origin now: the kernel bridges codered.navigate
  // onto /ws and the shell routes to /apps/codered client-side.
  fetch(`/api/apps/codered/navigate?path=${encodeURIComponent(path)}`, {
    method: "POST",
    credentials: "include",
  }).catch(() => {})
}

export function ChatView() {
  const { discussionId: urlDiscussionId } = useParams()
  const navigate = useNavigate()
  const disc = useDisc()

  // Intercept clicks on CodeRed links and navigate via API instead
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      const anchor = (e.target as HTMLElement).closest("a")
      if (!anchor) return
      const href = anchor.getAttribute("href")
      if (!href) return
      try {
        const url = new URL(href, window.location.origin)
        if (url.hostname === "localhost" && url.port === "18801") {
          e.preventDefault()
          navigateCodeRed(url.pathname + url.search)
        }
      } catch {}
    }
    document.addEventListener("click", handler)
    return () => document.removeEventListener("click", handler)
  }, [])

  const {
    discussions,
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
    setConfidential,
    resumeDiscussion,
    loadEarlierMessages,
    isLoadingMessages,
    hasEarlierMessages,
    isLoadingEarlier,
    upstreamConnected,
  } = disc

  const payloadSessionId = activeDiscussion?.sessionId ?? null
  const loadTranscriptPayload = useCallback<TranscriptPayloadLoader>((ref, range, signal) => {
    if (!payloadSessionId) return Promise.reject(new Error("This discussion has no active session"))
    const url = `/ai-session/sessions/${encodeURIComponent(payloadSessionId)}/messages/${ref.recordId}/output`
    return fetchTranscriptPayload(url, ref, range, signal)
  }, [payloadSessionId])
  const getTranscriptPayloadDownloadUrl = useCallback((ref: TranscriptPayloadRef) =>
    payloadSessionId
      ? `/ai-session/sessions/${encodeURIComponent(payloadSessionId)}/messages/${ref.recordId}/output?download=true`
      : "#",
  [payloadSessionId])

  useEffect(() => {
    if (urlDiscussionId && urlDiscussionId !== activeDiscussionId) {
      selectDiscussion(urlDiscussionId)
      setMobileTab(1)
    } else if (!urlDiscussionId && activeDiscussionId) {
      clearDiscussionSelection()
      setMobileTab(0)
    }
  }, [urlDiscussionId, activeDiscussionId, selectDiscussion, clearDiscussionSelection])

  useBreadcrumbLabel(
    urlDiscussionId ? `/apps/nova/chat/${urlDiscussionId}` : undefined,
    activeDiscussion?.type === "live"
      ? "Live"
      : activeDiscussion?.type === "heartbeat"
        ? "Heartbeat"
        : activeDiscussion?.title || "New Discussion",
  )

  const { agents, getAgent } = useAgents()
  const multiAgent = agents.length > 1
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter

  const filteredDiscussions = getSidebarDiscussionOrder(discussions, agentFilter)

  const pendingContext = useNovaPendingContext()
  const activeAgent = activeDiscussion ? getAgent(activeDiscussion.agentId) : undefined
  const sessionStats = useSessionStats(activeDiscussion?.sessionId, isStreaming, activeDiscussion)
  const share = useShare(activeDiscussion?.entityId)
  const [shareDialogOpen, setShareDialogOpen] = useState(false)
  const [mobileTab, setMobileTab] = useState(0)
  const [qualityTiers, setQualityTiers] = useState<QualityTierInfo[]>([])
  const [providers, setProviders] = useState<ProviderInfo[]>([])

  useEffect(() => {
    api.get<{ tiers: QualityTierInfo[] }>("/ai-session/quality-modes")
      .then(data => setQualityTiers(data.tiers ?? []))
      .catch(() => {})
    api.get<ProviderInfo[]>("/ai-session/providers/configured")
      .then(data => setProviders(Array.isArray(data) ? data : []))
      .catch(() => {})
  }, [])

  const { wrapMessage, clear: clearContext } = pendingContext
  const attachmentTransport = useMemo<AttachmentTransport>(() => ({
    async upload(file) {
      const form = new FormData()
      form.append("file", file, file.name)
      const response = await fetch("/ai-session/input-attachments", { method: "POST", credentials: "include", body: form })
      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: response.statusText }))
        throw new Error(error.message ?? error.error ?? response.statusText)
      }
      return response.json() as Promise<UploadedAttachment>
    },
    async delete(attachmentId) {
      const response = await fetch(`/ai-session/input-attachments/${encodeURIComponent(attachmentId)}`, { method: "DELETE", credentials: "include" })
      if (!response.ok && response.status !== 404) throw new Error("The attachment could not be removed")
    },
    getDownloadUrl(attachment) { return attachment.downloadUrl },
  }), [])
  const handleSend = useCallback((content: string, images?: ImageAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    const wrapped = wrapMessage(content, images)
    const sending = sendMessage(activeDiscussionId, wrapped.text, wrapped.images, options?.inputMethod)
    clearContext()
    return sending
  }, [activeDiscussionId, sendMessage, wrapMessage, clearContext])

  const handleSendInput = useCallback(async (input: ChatInputPart[], attachments: UploadedAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    const text = input
      .filter((part): part is Extract<ChatInputPart, { type: "text" }> => part.type === "text")
      .map(part => part.text)
      .join("\n")
    const wrapped = wrapMessage(text)
    const contextAttachments: UploadedAttachment[] = []
    for (const image of wrapped.images ?? []) {
      const blob = await fetch(`data:${image.mediaType};base64,${image.base64}`).then(response => response.blob())
      contextAttachments.push(await attachmentTransport.upload(new File([blob], "context-image", { type: image.mediaType })))
    }
    const allAttachments = [...contextAttachments, ...attachments]
    const wrappedInput: ChatInputPart[] = [{ type: "text", text: wrapped.text }]
    wrappedInput.push(...allAttachments.map(attachment => ({ type: "attachment" as const, attachmentId: attachment.id })))
    try {
      await sendInput(activeDiscussionId, wrappedInput, allAttachments, options?.inputMethod)
      clearContext()
    } catch (error) {
      for (const attachment of contextAttachments) void attachmentTransport.delete(attachment.id).catch(() => {})
      throw error
    }
  }, [activeDiscussionId, attachmentTransport, clearContext, sendInput, wrapMessage])

  useEffect(() => {
    const ctx = pendingContext.context
    if (!ctx?.question || !activeDiscussionId) return
    const text = formatContextMessage(ctx, ctx.question)
    const images = ctx.screenshot ? [ctx.screenshot] : undefined
    void sendMessage(activeDiscussionId, text, images).catch(() => {})
    clearContext()
  }, [pendingContext.context, activeDiscussionId, sendMessage, clearContext])

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

  const handleSelectDiscussion = useCallback((id: string) => {
    navigate(`/apps/nova/chat/${id}`)
    setMobileTab(1)
  }, [navigate])

  // Discussion-activity events link back to the discussion they describe.
  const resolveEventLink = useCallback((event: ParsedEvent) => {
    if (event.key !== "discussion") return undefined
    const discussionId = event.data?.discussionId
    if (typeof discussionId !== "string" || !discussionId) return undefined
    return () => handleSelectDiscussion(discussionId)
  }, [handleSelectDiscussion])

  const openNewDiscussion = useCallback(() => {
    window.dispatchEvent(new CustomEvent("nova:open-new-discussion"))
  }, [])

  const upstreamBanner = !upstreamConnected && (
    <div className="flex items-center gap-2 px-4 py-2 bg-accent-teal-a15 border-b border-overlay-6 text-text-muted text-sm">
      <i className="ph-bold ph-arrows-clockwise animate-spin" />
      <span>Reconnecting to AI service…</span>
    </div>
  )

  const handleConfidentialToggle = useCallback((checked: boolean) => {
    if (!activeDiscussionId) return
    setConfidential(activeDiscussionId, checked)
  }, [activeDiscussionId, setConfidential])

  const handleShare = useCallback(() => {
    share.reset()
    share.createShare()
    setShareDialogOpen(true)
  }, [share])

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
          style={{ height: "100%" }}
        >
          <i
            aria-hidden="true"
            className={`ph-bold ph-broadcast text-[12px] ${liveHeartbeatPair.live.status === "thinking" ? "animate-pulse" : ""}`}
            style={{ color: "var(--color-status-live)" }}
          />
          Live
          {activeDiscussion?.type === "live" && (
            <span aria-hidden="true" className="absolute inset-x-1 bottom-[-2px] h-0.5 rounded-full bg-primary" />
          )}
        </TabsTrigger>
        <TabsTrigger
          value="heartbeat"
          aria-label={liveHeartbeatPair.heartbeat.status === "thinking" ? "Heartbeat, tick running" : "Heartbeat"}
          style={{ height: "100%" }}
        >
          <i
            aria-hidden="true"
            className={`ph-bold ph-heartbeat text-[12px] ${liveHeartbeatPair.heartbeat.status === "thinking" ? "animate-pulse" : ""}`}
            style={{ color: "var(--color-accent-gold)" }}
          />
          Heartbeat
          {activeDiscussion?.type === "heartbeat" && (
            <span aria-hidden="true" className="absolute inset-x-1 bottom-[-2px] h-0.5 rounded-full bg-primary" />
          )}
        </TabsTrigger>
      </TabsList>
    </Tabs>
  )

  const chatHeader = activeDiscussion && (
    <PanelHeader
      leading={
        liveHeartbeatTabs ? <div className="flex flex-1 min-w-0 self-stretch">{liveHeartbeatTabs}</div> : (
          <EditableTitle
            title={activeDiscussion.title || "New discussion"}
            onRename={(title) => renameDiscussion(activeDiscussion.id, title)}
          />
        )
      }
    >
      {!activeDiscussion.confidential && (
        <button
          onClick={handleShare}
          className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10"
          title="Share conversation"
        >
          <i className="ph-bold ph-share-network text-xs" />
          <span>Share</span>
        </button>
      )}
      <ContextIndicator
        stats={sessionStats}
        messages={activeMessages}
        agent={activeAgent ? { id: activeAgent.id, name: activeAgent.name, avatarUrl: activeAgent.avatarUrl } : null}
        qualityTierOptions={qualityTiers.map(t => ({ value: t.slug, label: t.label, color: t.color, icon: t.icon }))}
        providerOptions={providers.map(p => ({
          value: p.slug,
          aliases: [p.backend],
          label: p.name,
          color: p.color,
          icon: p.icon,
          iconSvgPath: p.iconSvgPath,
        }))}
      >
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
            aria-label="Confidential discussion"
          />
        </div>
      </ContextIndicator>
    </PanelHeader>
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
        title="New discussion (Ctrl+N)"
      >
        <i className="ph-bold ph-plus text-xs" />
        <span>New</span>
      </button>
    </PanelHeader>
  )

  const renderStatusLine = useCallback(({ isStreaming, messages }: { isStreaming: boolean; messages: import("@redbamboo/chat").MessageBlock[] }) => (
    <NovaStatusLine isStreaming={isStreaming} messages={messages} />
  ), [])

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

  const { opacity: avatarOpacity } = useAvatarStyle()
  const { showAvatar: avatarEnabled } = useLocalSettings()
  const showAvatar = avatarEnabled && !!activeDiscussion
  const [avatarVersion, setAvatarVersion] = useState(0)
  useEffect(() => {
    const handler = () => setAvatarVersion(v => v + 1)
    window.addEventListener("nova:avatar-changed", handler)
    return () => window.removeEventListener("nova:avatar-changed", handler)
  }, [])
  const avatarBase = activeAgent ? activeAgent.avatarUrl : "/api/apps/nova/avatar"
  const avatarSrc = avatarVersion ? `${avatarBase}?v=${avatarVersion}` : avatarBase

  const chatArea = activeDiscussion ? (
    <div className="flex-1 flex flex-col min-h-0 min-w-0 relative">
      {isLoadingMessages && activeMessages.length === 0 && (
        <div className="absolute inset-0 flex items-center justify-center z-10">
          <i className="ph-bold ph-spinner animate-spin text-2xl opacity-40" />
        </div>
      )}
      <ChatPanel
        messages={activeMessages}
        isStreaming={isStreaming}
        interrupting={isInterrupting}
        resumePending={isResumePending}
        onSend={handleSend}
        onSendInput={handleSendInput}
        attachmentTransport={attachmentTransport}
        enableFileAttachments
        onInterrupt={handleInterrupt}
        sessionId={activeDiscussionId}
        draftStorageKey="nova-drafts"
        disabled={activeDiscussion.status === "archived" || (activeDiscussion.status === "stopped" && activeDiscussion.type !== "live")}
        hideComposer={activeDiscussion.type === "heartbeat"}
        pendingQuestion={pendingQuestion}
        questionOutcome={questionOutcome}
        onAnswerQuestion={handleAnswerQuestion}
        onResume={activeDiscussion.status === "stopped" && activeDiscussion.type !== "live" ? handleResume : undefined}
        hasEarlierMessages={hasEarlierMessages}
        onLoadEarlier={handleLoadEarlier}
        isLoadingEarlier={isLoadingEarlier}
        placeholder={`Talk to ${activeAgent?.name ?? "Nova"}...`}
        header={<>{chatHeader}{upstreamBanner}</>}
        speechBackend={speechBackend}
        resolveImageSrc={resolveImageSrc}
        resolveFileLink={resolveFileLink}
        resolveEventLink={resolveEventLink}
        loadTranscriptPayload={loadTranscriptPayload}
        getTranscriptPayloadDownloadUrl={getTranscriptPayloadDownloadUrl}
        assistantAvatar={avatarSrc}
        resolveAgentInfo={resolveAgentInfo}
        renderStatusLine={renderStatusLine}
        renderSideActions={renderSideActions}
      />
    </div>
  ) : (
    <div className="flex-1 flex items-center justify-center text-text-muted">
      {isSpawning ? (
        <div className="text-center">
          <i className="ph-bold ph-spinner animate-spin text-2xl mx-auto mb-3 opacity-40" />
          <p className="text-sm">Starting discussion…</p>
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
    <div className="relative h-full w-full">
      {showAvatar && (
        <div
          className="absolute top-4 left-1/2 -translate-x-1/2 w-[80px] h-[80px] z-20 rounded-full overflow-hidden md:hidden p-1.5"
          style={{ backgroundColor: "var(--background)" }}
        >
          <img
            src={avatarSrc}
            alt=""
            className="w-full h-full rounded-full object-cover object-top"
            style={{ opacity: avatarOpacity }}
          />
          <PluginExtensionSlot
            targetPluginId="nova"
            slotId="chat-avatar-overlay"
            context={{
              agentId: activeDiscussion?.agentId ?? null,
              discussionId: activeDiscussionId,
              variant: "mobile",
            }}
          />
        </div>
      )}
      <MasterDetailLayout
        layoutKey="nova-discussions"
        mobileLabels={["Discussions", "Chat"]}
        mobileTab={mobileTab}
        onMobileTabChange={setMobileTab}
        sidebar={
        <>
          {sidebarHeader}
          <div className="flex-1 overflow-hidden">
            <DiscussionSidebar
              discussions={filteredDiscussions}
              activeDiscussionId={urlDiscussionId ?? activeDiscussionId}
              onSelect={handleSelectDiscussion}
              onArchive={archiveDiscussion}
              onRotate={rotateDiscussion}
              onDismiss={dismissDiscussion}
              getAgent={getAgent}
              multiAgent={multiAgent}
            />
          </div>
          {showAvatar && (
            <div
              className="flex justify-center px-3 pb-5 pt-2 w-full"
              style={{ opacity: avatarOpacity }}
            >
              <div className="relative w-full max-w-[256px] aspect-square rounded-full overflow-hidden" style={{ backgroundColor: "var(--background)" }}>
                <img
                  src={avatarSrc}
                  alt=""
                  className="w-full h-full rounded-full object-cover object-top transition-opacity duration-500"
                />
                <PluginExtensionSlot
                  targetPluginId="nova"
                  slotId="chat-avatar-overlay"
                  context={{
                    agentId: activeDiscussion?.agentId ?? null,
                    discussionId: activeDiscussionId,
                    variant: "sidebar",
                  }}
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
