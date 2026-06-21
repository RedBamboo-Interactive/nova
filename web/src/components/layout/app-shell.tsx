import { useState } from "react"
import { useLocation, useNavigate, useMatches } from "react-router-dom"
import {
  Breadcrumb,
  DropdownMenuItem,
  NavTabs,
  NavTab,
  ResizablePanelGroup,
  ResizablePanel,
  ResizableHandle,
} from "@redbamboo/ui"
import {
  AppShell as Shell,
  useCommand,
  useLogStream,
  LogPanel,
  buildBreadcrumbs,
  useBreadcrumbLabelsContext,
  useNavigateUp,
} from "@redbamboo/utility"
import type { AppShellConfig, RouteMatch } from "@redbamboo/utility"
import { SettingsPanel } from "@/components/layout/settings-panel"
import { SearchOverlay } from "@/components/discussion/search-overlay"
import { useDisc } from "@/App"

const shellConfig: AppShellConfig = {
  name: "Nova",
  version: __APP_VERSION__,
  description: "Persistent AI companion",
  icon: "fa-solid fa-star",
  brand: {
    icon: "fa-solid fa-star",
    nameParts: ["No", "va"],
    accentClass: "text-primary",
  },
  github: {
    app: "https://github.com/RedBamboo-Interactive/nova",
    company: "https://github.com/RedBamboo-Interactive",
  },
}

function SettingsCommand({ onSettings }: { onSettings: () => void }) {
  useCommand("toggle-settings", {
    label: "Toggle Settings",
    description: "Show or hide the settings side panel",
    group: "App",
    keywords: ["preferences", "config", "theme", "identity"],
    action: onSettings,
  })
  return null
}

function ConsoleCommand({ onToggle }: { onToggle: () => void }) {
  useCommand("toggle-console", {
    label: "Toggle Console",
    description: "Show or hide the live log console",
    group: "App",
    keywords: ["logs", "debug", "errors"],
    action: onToggle,
  })
  return null
}

function TabCommands({ navigate }: { navigate: (path: string) => void }) {
  useCommand("tab-chat", {
    label: "Go to Chat",
    description: "Open the chat panel with the discussion list",
    group: "Navigation",
    shortcut: "F1",
    keywords: ["chat", "discussions"],
    action: () => navigate("/chat"),
  })
  useCommand("tab-pulse", {
    label: "Go to Pulse",
    description: "Open the Pulse panel listing automations and watchers",
    group: "Navigation",
    shortcut: "F2",
    keywords: ["pulse", "automations", "heartbeats"],
    action: () => navigate("/pulse"),
  })
  useCommand("tab-journal", {
    label: "Go to Journal",
    description: "Open the Journal panel to browse memory files",
    group: "Navigation",
    shortcut: "F3",
    keywords: ["journal", "memory", "files"],
    action: () => navigate("/journal"),
  })
  useCommand("trigger-automation", {
    label: "Trigger Automation",
    description: "Open the Pulse panel to run an automation now (each detail view has a Run Now button)",
    group: "Automations",
    keywords: ["run", "automation", "trigger", "pulse", "now"],
    action: () => navigate("/pulse"),
  })
  return null
}

/**
 * Discussion commands live at the shell level so Ctrl+N etc. work from any
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

  useCommand("new-discussion", {
    label: "New Discussion",
    description: "Start a new discussion and open it in the chat panel",
    group: "Discussions",
    shortcut: "Ctrl+N",
    keywords: ["start", "create", "new", "chat"],
    action: async () => {
      const d = await createDiscussion()
      if (d) navigate(`/chat/${d.id}`)
    },
  })

  useCommand("switch-discussion", {
    label: "Next Discussion",
    description: "Cycle to the next discussion in the sidebar",
    group: "Discussions",
    shortcut: "Alt+ArrowDown",
    keywords: ["switch", "cycle", "next", "tab"],
    action: () => {
      if (discussions.length === 0) return
      const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
      const next = discussions[(idx + 1) % discussions.length]
      if (next) navigate(`/chat/${next.id}`)
    },
  })

  useCommand("switch-discussion-prev", {
    label: "Previous Discussion",
    description: "Cycle to the previous discussion in the sidebar",
    group: "Discussions",
    shortcut: "Alt+ArrowUp",
    keywords: ["switch", "cycle", "previous", "prev", "tab"],
    action: () => {
      if (discussions.length === 0) return
      const idx = discussions.findIndex((d) => d.id === activeDiscussionId)
      const prev = discussions[(idx - 1 + discussions.length) % discussions.length]
      if (prev) navigate(`/chat/${prev.id}`)
    },
  })

  useCommand("close-discussion", {
    label: "Archive Discussion",
    description: "Archive the active discussion and stop its session",
    group: "Discussions",
    shortcut: "Alt+W",
    keywords: ["close", "archive", "remove"],
    action: () => {
      if (!activeDiscussionId) return
      archiveDiscussion(activeDiscussionId)
      navigate("/chat")
    },
  })

  useCommand("resume-discussion", {
    label: "Resume Discussion",
    description: "Restart the stopped session behind the active discussion",
    group: "Discussions",
    keywords: ["resume", "restart", "continue"],
    enabled: activeDiscussion?.status === "stopped",
    action: async () => {
      if (!activeDiscussionId) return
      await resumeDiscussion(activeDiscussionId)
      navigate(`/chat/${activeDiscussionId}`)
    },
  })

  useCommand("interrupt-discussion", {
    label: "Interrupt Response",
    description: "Stop the current response in the active discussion (Escape in the chat composer does the same)",
    group: "Discussions",
    keywords: ["stop", "cancel", "interrupt", "abort", "escape"],
    enabled: isStreaming,
    action: () => {
      if (activeDiscussionId) interruptDiscussion(activeDiscussionId)
    },
  })

  useCommand("export-conversations", {
    label: "Export Conversations",
    description: "Download the last 7 days of conversations as a markdown file",
    group: "Discussions",
    keywords: ["export", "download", "markdown", "backup", "save"],
    action: async () => {
      const res = await fetch("/api/discussions/export", { credentials: "include" })
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
  useCommand("search-conversations", {
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

export function AppShell({ children }: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
  const [searchOpen, setSearchOpen] = useState(false)
  const logStream = useLogStream()
  const location = useLocation()
  const navigate = useNavigate()
  const matches = useMatches()

  const labelCtx = useBreadcrumbLabelsContext()
  const crumbs = buildBreadcrumbs(matches as RouteMatch[], labelCtx?.labels)

  const getParentPath = () => {
    if (crumbs.length >= 2) return crumbs[crumbs.length - 2]!.href ?? null
    return null
  }
  useNavigateUp({ getParentPath, navigate })

  const breadcrumb = crumbs.length > 1 ? (
    <Breadcrumb items={crumbs} onNavigate={navigate} />
  ) : null

  return (
    <Shell
      config={shellConfig}
      breadcrumb={breadcrumb}
      headerContent={
        <NavTabs>
          <NavTab
            active={location.pathname.startsWith("/chat")}
            icon="fa-solid fa-comment"
            onClick={() => navigate("/chat")}
          >
            Chat
          </NavTab>
          <NavTab
            active={location.pathname.startsWith("/pulse")}
            icon="fa-solid fa-heart-pulse"
            onClick={() => navigate("/pulse")}
          >
            Pulse
          </NavTab>
          <NavTab
            active={location.pathname.startsWith("/journal")}
            icon="fa-solid fa-book"
            onClick={() => navigate("/journal")}
          >
            Journal
          </NavTab>
        </NavTabs>
      }
      menuItems={
        <>
          <DropdownMenuItem onClick={() => setConsoleOpen((prev) => !prev)}>
            <i className="fa-solid fa-terminal size-4 text-center" />
            Console
            {logStream.errorCount > 0 && (
              <span className="ml-auto text-[10px] text-destructive font-medium">
                {logStream.errorCount}
              </span>
            )}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => setSettingsOpen((prev) => !prev)}>
            <i className="fa-solid fa-gear size-4 text-center" />
            Settings
          </DropdownMenuItem>
        </>
      }
    >
      <TabCommands navigate={navigate} />
      <DiscussionCommands navigate={navigate} />
      <SearchCommand onOpen={() => setSearchOpen(true)} />
      <SettingsCommand onSettings={() => setSettingsOpen((prev) => !prev)} />
      <ConsoleCommand onToggle={() => setConsoleOpen((prev) => !prev)} />
      {searchOpen && (
        <SearchOverlay
          onClose={() => setSearchOpen(false)}
          onNavigate={(id) => {
            setSearchOpen(false)
            navigate(`/chat/${id}`)
          }}
        />
      )}
      {(consoleOpen || settingsOpen) ? (
        <ResizablePanelGroup orientation="horizontal" className="flex-1 min-h-0">
          <ResizablePanel defaultSize={consoleOpen && settingsOpen ? 55 : 75} minSize={30}>
            <main className="h-full overflow-hidden">{children}</main>
          </ResizablePanel>
          {consoleOpen && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel defaultSize={settingsOpen ? 20 : 25} minSize={15}>
                <LogPanel
                  entries={logStream.entries}
                  connected={logStream.connected}
                  paused={logStream.paused}
                  onPauseChange={logStream.setPaused}
                  onClear={logStream.clear}
                  onRefresh={() => logStream.refresh()}
                  errorCount={logStream.errorCount}
                  warnCount={logStream.warnCount}
                />
              </ResizablePanel>
            </>
          )}
          {settingsOpen && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel defaultSize={consoleOpen ? 25 : 25} minSize={15}>
                <SettingsPanel onClose={() => setSettingsOpen(false)} />
              </ResizablePanel>
            </>
          )}
        </ResizablePanelGroup>
      ) : (
        <main className="flex-1 min-h-0 overflow-hidden">{children}</main>
      )}
    </Shell>
  )
}
