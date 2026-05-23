import { useState, useMemo, useEffect } from "react"
import { NovaChatBackend } from "@/backends/NovaChatBackend"
import { AppShell, type Tab } from "@/components/layout/app-shell"
import { ChatView } from "@/panels/ChatView"
import { HeartbeatsPanel } from "@/panels/HeartbeatsPanel"
import { MemoryPanel } from "@/panels/MemoryPanel"
import { useLocalSettings } from "@/hooks/use-local-settings"

export function App() {
  const [activeTab, setActiveTab] = useState<Tab>("chat")
  const backend = useMemo(() => new NovaChatBackend(), [])
  const settings = useLocalSettings()

  useEffect(() => {
    const root = document.documentElement
    root.classList.toggle("dark", settings.theme === "dark")
    root.dataset.contrast = settings.contrast
  }, [settings.theme, settings.contrast])

  return (
    <AppShell activeTab={activeTab} onTabChange={setActiveTab}>
      <div className="h-full overflow-hidden">
        {activeTab === "chat" && <ChatView backend={backend} />}
        {activeTab === "heartbeats" && <HeartbeatsPanel />}
        {activeTab === "memory" && <MemoryPanel />}
      </div>
    </AppShell>
  )
}
