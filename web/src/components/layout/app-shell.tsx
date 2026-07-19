import { useState, useEffect, useCallback } from "react"
import { useNavigate } from "react-router-dom"
import { useCommand } from "@redbamboo/utility"
import { SearchOverlay } from "../../components/discussion/search-overlay"
import { NewDiscussionPicker } from "../../components/new-discussion-picker"
import { useDisc } from "../../App"

/**
 * Discussion commands live at the Nova shell level so Ctrl+N etc. work from any
 * panel (Pulse, Journal) — each action navigates to the chat panel as needed.
 */
function DiscussionCommands({ navigate, onNewDiscussion }: { navigate: (path: string) => void; onNewDiscussion: () => void }) {
  const {
    discussions,
    activeDiscussion,
    activeDiscussionId,
    isStreaming,
    archiveDiscussion,
    resumeDiscussion,
    interruptDiscussion,
  } = useDisc()

  useCommand("nova-new-discussion", {
    label: "New Discussion",
    description: "Start a new discussion and open it in the chat panel",
    group: "Discussions",
    shortcut: "Ctrl+N",
    keywords: ["start", "create", "new", "chat"],
    action: onNewDiscussion,
  })

  useCommand("nova-switch-discussion", {
    label: "Next Discussion",
    description: "Cycle to the next discussion in the sidebar",
    group: "Discussions",
    shortcut: "Alt+ArrowDown",
    keywords: ["switch", "cycle", "next", "tab"],
    action: () => {
      if (discussions.length === 0) return
      const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
      const next = discussions[(idx + 1) % discussions.length]
      if (next) navigate(`/apps/nova/chat/${next.id}`)
    },
  })

  useCommand("nova-switch-discussion-prev", {
    label: "Previous Discussion",
    description: "Cycle to the previous discussion in the sidebar",
    group: "Discussions",
    shortcut: "Alt+ArrowUp",
    keywords: ["switch", "cycle", "previous", "prev", "tab"],
    action: () => {
      if (discussions.length === 0) return
      const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
      const prev = discussions[(idx - 1 + discussions.length) % discussions.length]
      if (prev) navigate(`/apps/nova/chat/${prev.id}`)
    },
  })

  useCommand("nova-close-discussion", {
    label: "Archive Discussion",
    description: "Archive the active discussion and stop its session",
    group: "Discussions",
    shortcut: "Alt+W",
    keywords: ["close", "archive", "remove"],
    action: () => {
      if (!activeDiscussionId) return
      archiveDiscussion(activeDiscussionId)
      navigate("/apps/nova/chat")
    },
  })

  useCommand("nova-resume-discussion", {
    label: "Resume Discussion",
    description: "Restart the stopped session behind the active discussion",
    group: "Discussions",
    keywords: ["resume", "restart", "continue"],
    enabled: activeDiscussion?.status === "stopped",
    action: async () => {
      if (!activeDiscussionId) return
      await resumeDiscussion(activeDiscussionId)
      navigate(`/apps/nova/chat/${activeDiscussionId}`)
    },
  })

  useCommand("nova-interrupt-discussion", {
    label: "Interrupt Response",
    description: "Stop the current response in the active discussion (Escape in the chat composer does the same)",
    group: "Discussions",
    keywords: ["stop", "cancel", "interrupt", "abort", "escape"],
    enabled: isStreaming,
    action: () => {
      if (activeDiscussionId) interruptDiscussion(activeDiscussionId)
    },
  })

  useCommand("nova-export-conversations", {
    label: "Export Conversations",
    description: "Download the last 7 days of conversations as a markdown file",
    group: "Discussions",
    keywords: ["export", "download", "markdown", "backup", "save"],
    action: async () => {
      const res = await fetch("/api/apps/nova/discussions/export", { credentials: "include" })
      if (!res.ok) return
      const blob = new Blob([await res.text()], { type: "text/markdown" })
      const url = URL.createObjectURL(blob)
      const a = document.createElement("a")
      a.href = url
      a.download = `nova-conversations-${new Date().toISOString().slice(0, 10)}.md`
      a.click()
      URL.revokeObjectURL(url)
    },
  })

  return null
}

function SearchCommand({ onOpen }: { onOpen: () => void }) {
  useCommand("nova-search-conversations", {
    label: "Search Conversations",
    description: "Full-text search across all discussion content; click a result to open it",
    group: "Discussions",
    shortcut: "Ctrl+Shift+F",
    keywords: ["search", "find", "conversations", "history"],
    action: onOpen,
  })
  return null
}

interface Props {
  children: React.ReactNode
}

/**
 * Nova's internal chrome inside the Leaf shell. Tab navigation (Chat/Pulse/Journal)
 * is now handled by the shell via app-section entities declared in plugin.json.
 * This component provides conversation search, settings panel, and discussion commands.
 */
export function AppShell({ children }: Props) {
  const [searchOpen, setSearchOpen] = useState(false)
  const [pickerOpen, setPickerOpen] = useState(false)
  const navigate = useNavigate()
  const { createDiscussion } = useDisc()

  const openPicker = useCallback(() => setPickerOpen(true), [])

  useEffect(() => {
    window.addEventListener("nova:open-new-discussion", openPicker)
    return () => {
      window.removeEventListener("nova:open-new-discussion", openPicker)
    }
  }, [openPicker])

  const handlePickerSelect = useCallback(async (agentId: string, qualityTier: string, provider?: string) => {
    const d = await createDiscussion(agentId, qualityTier, provider)
    if (d) navigate(`/apps/nova/chat/${d.id}`)
  }, [createDiscussion, navigate])

  return (
    <div className="flex flex-col h-full min-h-0">
      <DiscussionCommands navigate={navigate} onNewDiscussion={openPicker} />
      <SearchCommand onOpen={() => setSearchOpen(true)} />
      <NewDiscussionPicker
        open={pickerOpen}
        onClose={() => setPickerOpen(false)}
        onSelect={handlePickerSelect}
      />
      {searchOpen && (
        <SearchOverlay
          onClose={() => setSearchOpen(false)}
          onNavigate={(id) => {
            setSearchOpen(false)
            navigate(`/apps/nova/chat/${id}`)
          }}
        />
      )}
      <main className="flex-1 min-h-0 overflow-hidden">{children}</main>
    </div>
  )
}
