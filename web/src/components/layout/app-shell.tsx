import { useState } from "react"
import { useLocation, useNavigate } from "react-router-dom"
import {
  Button,
  NavTabs,
  NavTab,
  ResizablePanelGroup,
  ResizablePanel,
  ResizableHandle,
} from "@redbamboo/ui"
import { useCommand } from "@redbamboo/utility"
import { SettingsPanel } from "../../components/layout/settings-panel"
import { SearchOverlay } from "../../components/discussion/search-overlay"
import { useDisc } from "../../App"

function SettingsCommand({ onSettings }: { onSettings: () => void }) {
  useCommand("nova-toggle-settings", {
    label: "Toggle Nova Settings",
    description: "Show or hide the Nova settings side panel",
    group: "Nova",
    keywords: ["preferences", "config", "docker", "nova"],
    action: onSettings,
  })
  return null
}

function TabCommands({ navigate }: { navigate: (path: string) => void }) {
  useCommand("nova-tab-chat", {
    label: "Go to Chat",
    description: "Open the chat panel with the discussion list",
    group: "Nova",
    shortcut: "F1",
    keywords: ["chat", "discussions"],
    action: () => navigate("/apps/nova/chat"),
  })
  useCommand("nova-tab-pulse", {
    label: "Go to Pulse",
    description: "Open the Pulse panel listing automations and watchers",
    group: "Nova",
    shortcut: "F2",
    keywords: ["pulse", "automations", "heartbeats"],
    action: () => navigate("/apps/nova/pulse"),
  })
  useCommand("nova-tab-journal", {
    label: "Go to Journal",
    description: "Open the Journal panel to browse memory files",
    group: "Nova",
    shortcut: "F3",
    keywords: ["journal", "memory", "files"],
    action: () => navigate("/apps/nova/journal"),
  })
  return null
}

/**
 * Discussion commands live at the Nova shell level so Ctrl+N etc. work from any
 * panel (Pulse, Journal) — each action navigates to the chat panel as needed.
 */
function DiscussionCommands({ navigate }: { navigate: (path: string) => void }) {
  const {
    discussions,
    activeDiscussion,
    activeDiscussionId,
    isStreaming,
    createDiscussion,
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
    action: async () => {
      const d = await createDiscussion()
      if (d) navigate(`/apps/nova/chat/${d.id}`)
    },
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
 * Nova's internal chrome inside the Leaf shell: the Chat/Pulse/Journal tab row,
 * conversation search, and the settings side panel. The outer shell (brand header,
 * command palette, console, breadcrumbs) belongs to the host.
 */
export function AppShell({ children }: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const location = useLocation()
  const navigate = useNavigate()

  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="flex items-center gap-2 px-3 pt-2 pb-1.5 shrink-0 border-b border-overlay-6">
        <NavTabs>
          <NavTab
            active={location.pathname.startsWith("/apps/nova/chat")}
            icon="fa-solid fa-comment"
            onClick={() => navigate("/apps/nova/chat")}
          >
            Chat
          </NavTab>
          <NavTab
            active={location.pathname.startsWith("/apps/nova/pulse")}
            icon="fa-solid fa-heart-pulse"
            onClick={() => navigate("/apps/nova/pulse")}
          >
            Pulse
          </NavTab>
          <NavTab
            active={location.pathname.startsWith("/apps/nova/journal")}
            icon="fa-solid fa-book"
            onClick={() => navigate("/apps/nova/journal")}
          >
            Journal
          </NavTab>
        </NavTabs>
        <div className="ml-auto flex items-center gap-1">
          <Button variant="ghost" size="icon-xs" title="Search conversations (Ctrl+Shift+F)" onClick={() => setSearchOpen(true)}>
            <i className="fa-solid fa-magnifying-glass text-xs" />
          </Button>
          <Button variant="ghost" size="icon-xs" title="Nova settings" onClick={() => setSettingsOpen((prev) => !prev)}>
            <i className="fa-solid fa-gear text-xs" />
          </Button>
        </div>
      </div>

      <TabCommands navigate={navigate} />
      <DiscussionCommands navigate={navigate} />
      <SearchCommand onOpen={() => setSearchOpen(true)} />
      <SettingsCommand onSettings={() => setSettingsOpen((prev) => !prev)} />
      {searchOpen && (
        <SearchOverlay
          onClose={() => setSearchOpen(false)}
          onNavigate={(id) => {
            setSearchOpen(false)
            navigate(`/apps/nova/chat/${id}`)
          }}
        />
      )}
      {settingsOpen ? (
        <ResizablePanelGroup orientation="horizontal" className="flex-1 min-h-0">
          <ResizablePanel defaultSize={75} minSize={30}>
            <main className="h-full overflow-hidden">{children}</main>
          </ResizablePanel>
          <ResizableHandle withHandle />
          <ResizablePanel defaultSize={25} minSize={15}>
            <SettingsPanel onClose={() => setSettingsOpen(false)} />
          </ResizablePanel>
        </ResizablePanelGroup>
      ) : (
        <main className="flex-1 min-h-0 overflow-hidden">{children}</main>
      )}
    </div>
  )
}
