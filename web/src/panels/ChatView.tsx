import { useState, useCallback, useEffect } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { MasterDetailLayout, PanelHeader, DropdownMenu, DropdownMenuTrigger, DropdownMenuContent, DropdownMenuItem } from "@redbamboo/ui"
import { ChatPanel, ContextIndicator, type ImageAttachment, type SendOptions } from "@redbamboo/chat"
import { useBreadcrumbLabel, formatContextMessage } from "@redbamboo/utility"
import { DiscussionSidebar } from "@/components/discussion/discussion-sidebar"
import { EditableTitle } from "@/components/discussion/editable-title"
import { AgentPicker } from "@/components/agent-picker"
import { NovaStatusLine } from "@/components/nova-status-line"
import { OutfitBrowser } from "@/components/outfit-browser"
import { createNovaSpeechBackend } from "@/lib/speech"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { useAgents } from "@/hooks/use-agents"
import { useDisc, useNovaPendingContext } from "@/App"
import { useSessionStats } from "@/hooks/use-session-stats"
import { setSettings } from "@/lib/settings-store"

const speechBackend = createNovaSpeechBackend()

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
    return `/api/file?path=${encodeURIComponent(src)}`
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
  fetch(`http://localhost:18801/api/navigate?path=${encodeURIComponent(path)}`, {
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
    isSpawning,
    pendingQuestion,
    selectDiscussion,
    createDiscussion,
    sendMessage,
    interruptDiscussion,
    answerQuestion,
    archiveDiscussion,
    dismissDiscussion,
    renameDiscussion,
    resumeDiscussion,
    upstreamConnected,
  } = disc

  useEffect(() => {
    if (urlDiscussionId && urlDiscussionId !== activeDiscussionId) {
      selectDiscussion(urlDiscussionId)
    }
  }, [urlDiscussionId, activeDiscussionId, selectDiscussion])

  useBreadcrumbLabel(
    urlDiscussionId ? `/chat/${urlDiscussionId}` : undefined,
    activeDiscussion?.title || "New Discussion",
  )

  const { agents, getAgent, defaultAgentId } = useAgents()
  const multiAgent = agents.length > 1
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter

  const filteredDiscussions = agentFilter
    ? discussions.filter((d) => d.agentId === agentFilter)
    : discussions

  const pendingContext = useNovaPendingContext()
  const sessionStats = useSessionStats(activeDiscussion?.sessionId, isStreaming)
  const [mobileTab, setMobileTab] = useState(0)

  const { wrapMessage, clear: clearContext } = pendingContext
  const handleSend = useCallback((content: string, images?: ImageAttachment[], options?: SendOptions) => {
    if (!activeDiscussionId) return
    const wrapped = wrapMessage(content, images)
    sendMessage(activeDiscussionId, wrapped.text, wrapped.images, options?.inputMethod)
    clearContext()
  }, [activeDiscussionId, sendMessage, wrapMessage, clearContext])

  useEffect(() => {
    const ctx = pendingContext.context
    if (!ctx?.question || !activeDiscussionId) return
    const text = formatContextMessage(ctx, ctx.question)
    const images = ctx.screenshot ? [ctx.screenshot] : undefined
    sendMessage(activeDiscussionId, text, images)
    clearContext()
  }, [pendingContext.context, activeDiscussionId, sendMessage, clearContext])

  const handleInterrupt = useCallback(() => {
    if (!activeDiscussionId) return
    interruptDiscussion(activeDiscussionId)
  }, [activeDiscussionId, interruptDiscussion])

  const handleAnswerQuestion = useCallback((answer: string) => {
    if (!activeDiscussionId) return
    answerQuestion(activeDiscussionId, answer)
  }, [activeDiscussionId, answerQuestion])

  const handleResume = useCallback(async () => {
    if (!activeDiscussionId) return
    await resumeDiscussion(activeDiscussionId)
  }, [activeDiscussionId, resumeDiscussion])

  const handleSelectDiscussion = useCallback((id: string) => {
    navigate(`/chat/${id}`)
    setMobileTab(1)
  }, [navigate])

  const handleNewDiscussion = useCallback(async (agentId?: string) => {
    const effectiveAgentId = agentId ?? agentFilter ?? defaultAgentId ?? undefined
    const d = await createDiscussion(effectiveAgentId)
    if (d) navigate(`/chat/${d.id}`)
    setMobileTab(1)
  }, [createDiscussion, navigate, agentFilter, defaultAgentId])

  const upstreamBanner = !upstreamConnected && (
    <div className="flex items-center gap-2 px-4 py-2 bg-accent-teal-a15 border-b border-overlay-6 text-text-muted text-sm">
      <i className="fa-solid fa-arrows-rotate fa-spin" />
      <span>Reconnecting to AI service…</span>
    </div>
  )

  const chatHeader = activeDiscussion && (
    <PanelHeader
      leading={
        <EditableTitle
          title={activeDiscussion.title || "New discussion"}
          onRename={(title) => renameDiscussion(activeDiscussion.id, title)}
        />
      }
    >
      <ContextIndicator stats={sessionStats} messages={activeMessages} />
    </PanelHeader>
  )

  const sidebarHeader = (
    <PanelHeader title={multiAgent ? undefined : "Discussions"}>
      {multiAgent && (
        <AgentPicker
          agents={agents}
          selectedId={agentFilter}
          onSelect={(id) => setSettings({ agentFilter: id })}
          showAll
        />
      )}
      {multiAgent ? (
        <DropdownMenu>
          <DropdownMenuTrigger className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10 cursor-pointer">
            <i className="fa-solid fa-plus text-xs" />
            <span>New</span>
            <i className="fa-solid fa-chevron-down text-[8px] opacity-50" />
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" sideOffset={4}>
            {agents.map((agent) => (
              <DropdownMenuItem key={agent.id} onClick={() => handleNewDiscussion(agent.id)}>
                <img
                  src={agent.avatarUrl}
                  alt={agent.name}
                  className="w-4 h-4 rounded object-cover"
                  onError={(e) => { e.currentTarget.style.display = "none" }}
                />
                {agent.name}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : (
        <button
          onClick={() => handleNewDiscussion()}
          className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10"
          title="New discussion"
        >
          <i className="fa-solid fa-plus text-xs" />
          <span>New</span>
        </button>
      )}
    </PanelHeader>
  )

  const resolveAgentInfo = useCallback((agentId: string) => {
    const agent = getAgent(agentId)
    if (!agent) return undefined
    return { name: agent.name, avatarUrl: agent.avatarUrl }
  }, [getAgent])

  const activeAgent = activeDiscussion ? getAgent(activeDiscussion.agentId) : undefined
  const { opacity: avatarOpacity } = useAvatarStyle()
  const { showAvatar: avatarEnabled } = useLocalSettings()
  const showAvatar = avatarEnabled && activeDiscussion && activeMessages.some(m => m.role === "assistant")
  const [outfitBrowserOpen, setOutfitBrowserOpen] = useState(false)
  const [avatarVersion, setAvatarVersion] = useState(0)
  useEffect(() => {
    const handler = () => setAvatarVersion(v => v + 1)
    window.addEventListener("nova:avatar-changed", handler)
    return () => window.removeEventListener("nova:avatar-changed", handler)
  }, [])
  const avatarBase = activeAgent ? activeAgent.avatarUrl : "/api/avatar"
  const avatarSrc = avatarVersion ? `${avatarBase}?v=${avatarVersion}` : avatarBase

  const chatArea = activeDiscussion ? (
    <div className="flex-1 flex flex-col min-h-0 min-w-0 relative">
      <ChatPanel
        messages={activeMessages}
        isStreaming={isStreaming}
        onSend={handleSend}
        onInterrupt={handleInterrupt}
        sessionId={activeDiscussionId}
        draftStorageKey="nova-drafts"
        disabled={activeDiscussion.status === "archived" || activeDiscussion.status === "stopped"}
        pendingQuestion={pendingQuestion}
        onAnswerQuestion={handleAnswerQuestion}
        onResume={activeDiscussion.status === "stopped" ? handleResume : undefined}
        placeholder={`Talk to ${activeAgent?.name ?? "Nova"}...`}
        header={<>{chatHeader}{upstreamBanner}</>}
        speechBackend={speechBackend}
        resolveImageSrc={resolveImageSrc}
        resolveFileLink={resolveFileLink}
        assistantAvatar={avatarSrc}
        resolveAgentInfo={resolveAgentInfo}
        renderStatusLine={({ isStreaming, messages }) => (
          <NovaStatusLine isStreaming={isStreaming} messages={messages} />
        )}
      />
    </div>
  ) : (
    <div className="flex-1 flex items-center justify-center text-text-muted">
      {isSpawning ? (
        <div className="text-center">
          <i className="fa-solid fa-spinner fa-spin text-2xl mx-auto mb-3 opacity-40" />
          <p className="text-sm">Starting discussion…</p>
        </div>
      ) : (
        <div className="text-center">
          <i className="fa-solid fa-star text-3xl mx-auto mb-3 opacity-30" />
          <p className="text-sm mb-4">Start a conversation</p>
          <button
            onClick={() => handleNewDiscussion()}
            className="text-xs px-3 py-1.5 rounded bg-overlay-10 hover:bg-overlay-15 text-contrast transition-colors"
          >
            <i className="fa-solid fa-plus mr-1.5" />
            New Discussion
          </button>
        </div>
      )}
    </div>
  )

  return (
    <div className="relative h-full w-full">
      {showAvatar && (
        <button
          onClick={() => setOutfitBrowserOpen(true)}
          className="absolute top-4 left-1/2 -translate-x-1/2 w-[80px] h-[80px] z-20 rounded-full overflow-hidden md:hidden p-1.5 cursor-pointer group"
          style={{ backgroundColor: "var(--background)" }}
          title="Browse outfits"
        >
          <img
            src={avatarSrc}
            alt=""
            className="w-full h-full rounded-full object-cover object-top"
            style={{ opacity: avatarOpacity }}
          />
          <div className="absolute inset-1.5 rounded-full bg-black/0 group-hover:bg-black/20 transition-colors flex items-center justify-center">
            <i className="fa-solid fa-shirt text-white/0 group-hover:text-white/70 text-xs transition-colors" />
          </div>
        </button>
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
              activeDiscussionId={activeDiscussionId}
              onSelect={handleSelectDiscussion}
              onArchive={archiveDiscussion}
              onDismiss={dismissDiscussion}
              getAgent={getAgent}
              multiAgent={multiAgent}
            />
          </div>
          {showAvatar && (
            <button
              onClick={() => setOutfitBrowserOpen(true)}
              className="hidden md:flex justify-center px-3 pb-5 pt-2 w-full cursor-pointer group"
              style={{ opacity: avatarOpacity }}
              title="Browse outfits"
            >
              <div className="relative w-full max-w-[256px] aspect-square rounded-full overflow-hidden" style={{ backgroundColor: "var(--background)" }}>
                <img
                  src={avatarSrc}
                  alt=""
                  className="w-full h-full rounded-full object-cover object-top transition-opacity duration-500"
                />
                <div className="absolute inset-0 rounded-full bg-black/0 group-hover:bg-black/20 transition-colors flex items-center justify-center">
                  <i className="fa-solid fa-shirt text-white/0 group-hover:text-white/70 text-lg transition-colors" />
                </div>
              </div>
            </button>
          )}
        </>
      }
      detail={chatArea}
    />
    {outfitBrowserOpen && (
      <OutfitBrowser
        onClose={() => setOutfitBrowserOpen(false)}
        discussionId={activeDiscussionId}
        agentId={activeDiscussion?.agentId}
      />
    )}
    </div>
  )
}
