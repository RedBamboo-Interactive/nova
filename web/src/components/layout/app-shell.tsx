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
import { SettingsModal } from "@/components/layout/settings-modal"

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
  useCommand("open-settings", {
    label: "Open Settings",
    group: "App",
    keywords: ["preferences", "config", "theme", "identity"],
    action: onSettings,
  })
  return null
}

function ConsoleCommand({ onToggle }: { onToggle: () => void }) {
  useCommand("toggle-console", {
    label: "Toggle Console",
    group: "App",
    keywords: ["logs", "debug", "errors"],
    action: onToggle,
  })
  return null
}

function TabCommands({ navigate }: { navigate: (path: string) => void }) {
  useCommand("tab-chat", {
    label: "Go to Chat",
    group: "Navigation",
    shortcut: "F1",
    keywords: ["chat", "discussions"],
    action: () => navigate("/chat"),
  })
  useCommand("tab-pulse", {
    label: "Go to Pulse",
    group: "Navigation",
    shortcut: "F2",
    keywords: ["pulse", "automations", "heartbeats"],
    action: () => navigate("/pulse"),
  })
  useCommand("tab-journal", {
    label: "Go to Journal",
    group: "Navigation",
    shortcut: "F3",
    keywords: ["journal", "memory", "files"],
    action: () => navigate("/journal"),
  })
  return null
}

interface Props {
  children: React.ReactNode
}

export function AppShell({ children }: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
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
          <DropdownMenuItem onClick={() => setSettingsOpen(true)}>
            <i className="fa-solid fa-gear size-4 text-center" />
            Settings
          </DropdownMenuItem>
        </>
      }
    >
      <TabCommands navigate={navigate} />
      <SettingsCommand onSettings={() => setSettingsOpen(true)} />
      <ConsoleCommand onToggle={() => setConsoleOpen((prev) => !prev)} />
      {consoleOpen ? (
        <ResizablePanelGroup orientation="horizontal" className="flex-1 min-h-0">
          <ResizablePanel defaultSize={75} minSize={30}>
            <main className="h-full overflow-hidden">{children}</main>
          </ResizablePanel>
          <ResizableHandle withHandle />
          <ResizablePanel defaultSize={25} minSize={15}>
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
        </ResizablePanelGroup>
      ) : (
        <main className="flex-1 min-h-0 overflow-hidden">{children}</main>
      )}
      <SettingsModal open={settingsOpen} onOpenChange={setSettingsOpen} />
    </Shell>
  )
}
