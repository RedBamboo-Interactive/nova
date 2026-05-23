import { useState, useEffect } from "react"
import { AppShell, type Tab } from "@/components/layout/app-shell"
import { ChatView } from "@/panels/ChatView"
import { HeartbeatsPanel } from "@/panels/HeartbeatsPanel"
import { MemoryPanel } from "@/panels/MemoryPanel"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { useDiscussions } from "@/hooks/use-discussions"

export function App() {
  const [activeTab, setActiveTab] = useState<Tab>("chat")
  const settings = useLocalSettings()
  const disc = useDiscussions()

  useEffect(() => {
    const root = document.documentElement
    root.classList.toggle("dark", settings.theme === "dark")
    root.dataset.contrast = settings.contrast
  }, [settings.theme, settings.contrast])

  return (
    <AppShell activeTab={activeTab} onTabChange={setActiveTab}>
      <div className="h-full overflow-hidden">
        {activeTab === "chat" && <ChatView disc={disc} />}
        {activeTab === "heartbeats" && <HeartbeatsPanel />}
        {activeTab === "memory" && <MemoryPanel />}
      </div>
    </AppShell>
  )
}
