import { useState } from "react"
import {
  DropdownMenuItem,
  NavTabs,
  NavTab,
  ResizablePanelGroup,
  ResizablePanel,
  ResizableHandle,
} from "@redbamboo/ui"
import { AppShell as Shell, useCommand, useLogStream, LogPanel } from "@redbamboo/utility"
import type { AppShellConfig } from "@redbamboo/utility"
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

export type Tab = "chat" | "automations" | "memory"

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

interface Props {
  children: React.ReactNode
  activeTab: Tab
  onTabChange: (tab: Tab) => void
}

export function AppShell({ children, activeTab, onTabChange }: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
  const logStream = useLogStream()

  return (
    <Shell
      config={shellConfig}
      headerContent={
        <NavTabs>
          <NavTab
            active={activeTab === "chat"}
            icon="fa-solid fa-comment"
            onClick={() => onTabChange("chat")}
          >
            <span className="hidden sm:inline">Chat</span>
          </NavTab>
          <NavTab
            active={activeTab === "automations"}
            icon="fa-solid fa-heart-pulse"
            onClick={() => onTabChange("automations")}
          >
            <span className="hidden sm:inline">Pulse</span>
          </NavTab>
          <NavTab
            active={activeTab === "memory"}
            icon="fa-solid fa-book"
            onClick={() => onTabChange("memory")}
          >
            <span className="hidden sm:inline">Journal</span>
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
