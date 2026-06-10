import { useState, useCallback, useEffect } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { MasterDetailLayout, PanelHeader } from "@redbamboo/ui"
import { ChatPanel, ContextIndicator, type ImageAttachment, type SendOptions } from "@redbamboo/chat"
import { useCommand, useBreadcrumbLabel, formatContextMessage } from "@redbamboo/utility"
import { DiscussionSidebar } from "@/components/discussion/discussion-sidebar"
import { NovaStatusLine } from "@/components/nova-status-line"
import { createNovaSpeechBackend } from "@/lib/speech"
import { useNovaEmotion } from "@/hooks/use-nova-emotion"
import { useDisc, useNovaPendingContext } from "@/App"
import { useSessionStats } from "@/hooks/use-session-stats"

const speechBackend = createNovaSpeechBackend()

const BASE_EYE_HUE = 300 // original magenta

function hexToHue(hex: string): number {
  const n = parseInt(hex.replace("#", ""), 16)
  const r = ((n >> 16) & 255) / 255
  const g = ((n >> 8) & 255) / 255
  const b = (n & 255) / 255
  const max = Math.max(r, g, b), min = Math.min(r, g, b)
  if (max === min) return 0
  const d = max - min
  let h = 0
  if (max === r) h = ((g - b) / d + 6) % 6
  else if (max === g) h = (b - r) / d + 2
  else h = (r - g) / d + 4
  return h * 60
}

function useAvatarStyle() {
  const [hueRotation, setHueRotation] = useState("0deg")
  const [opacity, setOpacity] = useState(0.9)
  useEffect(() => {
    const update = () => {
      const root = document.documentElement
      const brand = getComputedStyle(root).getPropertyValue("--brand").trim()
      if (brand) setHueRotation(`${Math.round(hexToHue(brand) - BASE_EYE_HUE)}deg`)
      setOpacity(root.dataset.contrast === "low" ? 0.7 : 0.9)
    }
    update()
    const observer = new MutationObserver(update)
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["style", "class", "data-contrast"] })
    return () => observer.disconnect()
  }, [])
  return { hueRotation, opacity }
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
    resumeDiscussion,
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

  const handleNewDiscussion = useCallback(async () => {
    const d = await createDiscussion()
    if (d) navigate(`/chat/${d.id}`)
    setMobileTab(1)
  }, [createDiscussion, navigate])

  useCommand("new-discussion", {
    label: "New Discussion",
    group: "Discussions",
    shortcut: "Ctrl+N",
    keywords: ["start", "create", "new", "chat"],
    action: handleNewDiscussion,
  })

  useCommand("switch-discussion", {
    label: "Next Discussion",
    group: "Discussions",
    shortcut: "Ctrl+Tab",
    keywords: ["switch", "cycle", "tab"],
    action: () => {
      if (discussions.length === 0) return
      const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
      const next = discussions[(idx + 1) % discussions.length]
      if (next) navigate(`/chat/${next.id}`)
    },
  })

  useCommand("close-discussion", {
    label: "Archive Discussion",
    group: "Discussions",
    shortcut: "Ctrl+W",
    keywords: ["close", "archive", "remove"],
    action: () => {
      if (activeDiscussionId) {
        archiveDiscussion(activeDiscussionId)
        navigate("/chat")
      }
    },
  })

  const chatHeader = activeDiscussion && (
    <PanelHeader title={activeDiscussion.title || "New discussion"}>
      <ContextIndicator stats={sessionStats} messages={activeMessages} />
    </PanelHeader>
  )

  const sidebarHeader = (
    <PanelHeader title="Discussions">
      <button
        onClick={handleNewDiscussion}
        className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10"
        title="New discussion"
      >
        <i className="fa-solid fa-plus text-xs" />
        <span>New</span>
      </button>
    </PanelHeader>
  )

  const { src: avatarSrc } = useNovaEmotion(activeMessages, isStreaming)
  const { hueRotation: eyeHueRotation, opacity: avatarOpacity } = useAvatarStyle()
  const showAvatar = activeDiscussion && activeMessages.some(m => m.role === "assistant")

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
        placeholder="Talk to Nova..."
        header={chatHeader}
        speechBackend={speechBackend}
        resolveImageSrc={resolveImageSrc}
        resolveFileLink={resolveFileLink}
        renderStatusLine={({ isStreaming, messages }) => (
          <NovaStatusLine isStreaming={isStreaming} messages={messages} />
        )}
      />
      {showAvatar && (
        <>
          {/* Desktop: left margin */}
          <div
            className="absolute w-48 h-48 z-10 pointer-events-none rounded-full overflow-hidden drop-shadow-lg hidden md:block"
            style={{
              left: "calc((50% - 384px) / 2 - 96px)",
              top: "50%",
              transform: "translateY(-50%)",
              opacity: avatarOpacity,
            }}
          >
            <img
              src={avatarSrc}
              alt=""
              className="w-full h-full rounded-full object-cover object-top transition-opacity duration-500"
              style={{ filter: `hue-rotate(${eyeHueRotation})` }}
            />
            <div
              className="absolute inset-0 rounded-full border-[1.5px] border-surface-elevated"
            />
          </div>
          {/* Mobile: top left under header */}
          <div
            className="absolute top-16 left-3 w-[92px] h-[92px] z-10 pointer-events-none rounded-full overflow-hidden drop-shadow-md md:hidden"
            style={{ backgroundColor: "var(--background)" }}
          >
            <img
              src={avatarSrc}
              alt=""
              className="w-full h-full rounded-full object-cover object-top"
              style={{ filter: `hue-rotate(${eyeHueRotation})`, opacity: avatarOpacity }}
            />
            <div
              className="absolute inset-0 rounded-full border border-surface-elevated"
            />
          </div>
        </>
      )}
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
          <p className="text-sm mb-4">Start a conversation with Nova</p>
          <button
            onClick={handleNewDiscussion}
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
              discussions={discussions}
              activeDiscussionId={activeDiscussionId}
              onSelect={handleSelectDiscussion}
              onArchive={archiveDiscussion}
              onDismiss={dismissDiscussion}
            />
          </div>
        </>
      }
      detail={chatArea}
    />
  )
}
