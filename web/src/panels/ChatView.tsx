import { useState, useCallback, useEffect } from "react"
import { MasterDetailLayout, PanelHeader } from "@redbamboo/ui"
import { ChatPanel, type ImageAttachment } from "@redbamboo/chat"
import { useCommand } from "@redbamboo/utility"
import { DiscussionSidebar } from "@/components/discussion/discussion-sidebar"
import { NovaStatusLine } from "@/components/nova-status-line"
import { createNovaSpeechBackend } from "@/lib/speech"
import type { useDiscussions } from "@/hooks/use-discussions"

const speechBackend = createNovaSpeechBackend()

function resolveImageSrc(src: string): string | undefined {
  if (/^[A-Za-z]:[\\\/]/.test(src))
    return `/api/file?path=${encodeURIComponent(src)}`
  return src
}

type DiscussionsHook = ReturnType<typeof useDiscussions>

interface Props {
  disc: DiscussionsHook
}

export function ChatView({ disc }: Props) {
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
  } = disc

  const [mobileTab, setMobileTab] = useState(0)

  const handleSend = useCallback((content: string, images?: ImageAttachment[]) => {
    if (!activeDiscussionId) return
    sendMessage(activeDiscussionId, content, images)
  }, [activeDiscussionId, sendMessage])

  const handleInterrupt = useCallback(() => {
    if (!activeDiscussionId) return
    interruptDiscussion(activeDiscussionId)
  }, [activeDiscussionId, interruptDiscussion])

  const handleAnswerQuestion = useCallback((answer: string) => {
    if (!activeDiscussionId) return
    answerQuestion(activeDiscussionId, answer)
  }, [activeDiscussionId, answerQuestion])

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.key === "n") {
        e.preventDefault()
        createDiscussion()
        setMobileTab(1)
      }
      if (e.ctrlKey && e.key === "w") {
        e.preventDefault()
        if (!activeDiscussionId) return
        archiveDiscussion(activeDiscussionId)
      }
      if (e.ctrlKey && !e.shiftKey && e.key === "Tab") {
        e.preventDefault()
        if (discussions.length === 0) return
        const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
        const next = discussions[(idx + 1) % discussions.length]
        if (next) selectDiscussion(next.id)
      }
    }
    window.addEventListener("keydown", onKeyDown)
    return () => window.removeEventListener("keydown", onKeyDown)
  }, [discussions, activeDiscussionId, selectDiscussion, createDiscussion, archiveDiscussion])

  useCommand("new-discussion", {
    label: "New Discussion",
    group: "Discussions",
    shortcut: "Ctrl+N",
    keywords: ["start", "create", "new", "chat"],
    action: () => { createDiscussion(); setMobileTab(1) },
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
      if (next) selectDiscussion(next.id)
    },
  })

  useCommand("close-discussion", {
    label: "Archive Discussion",
    group: "Discussions",
    shortcut: "Ctrl+W",
    keywords: ["close", "archive", "remove"],
    action: () => {
      if (activeDiscussionId) archiveDiscussion(activeDiscussionId)
    },
  })

  const chatHeader = activeDiscussion && (
    <PanelHeader title={activeDiscussion.title || "New discussion"} />
  )

  const sidebarHeader = (
    <PanelHeader title="Discussions">
      <button
        onClick={() => { createDiscussion(); setMobileTab(1) }}
        className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10"
        title="New discussion"
      >
        <i className="fa-solid fa-plus text-xs" />
        <span>New</span>
      </button>
    </PanelHeader>
  )

  const chatArea = activeDiscussion ? (
    <ChatPanel
      messages={activeMessages}
      isStreaming={isStreaming}
      onSend={handleSend}
      onInterrupt={handleInterrupt}
      sessionId={activeDiscussionId}
      disabled={activeDiscussion.status === "archived" || !activeDiscussion.sessionId}
      pendingQuestion={pendingQuestion}
      onAnswerQuestion={handleAnswerQuestion}
      placeholder="Talk to Nova..."
      header={chatHeader}
      speechBackend={speechBackend}
      resolveImageSrc={resolveImageSrc}
      renderStatusLine={({ isStreaming, messages }) => (
        <NovaStatusLine isStreaming={isStreaming} messages={messages} />
      )}
    />
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
            onClick={() => { createDiscussion(); setMobileTab(1) }}
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
              onSelect={(id) => { selectDiscussion(id); setMobileTab(1) }}
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
