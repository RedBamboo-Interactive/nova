import { useState, useEffect, useCallback, useRef } from "react"
import { WsEventProvider, useWsSubscribe } from "@redbamboo/utility"
import { AppShell, type Tab } from "@/components/layout/app-shell"
import { ChatView } from "@/panels/ChatView"
import { HeartbeatsPanel } from "@/panels/HeartbeatsPanel"
import { MemoryPanel } from "@/panels/MemoryPanel"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { useDiscussions } from "@/hooks/use-discussions"
import type { WsEvent } from "@/lib/types"

function WsDiscussionBridge({ discRef }: { discRef: React.RefObject<ReturnType<typeof useDiscussions>> }) {
  useWsSubscribe((event) => {
    discRef.current.handleWsEvent(event as WsEvent)
  })
  return null
}

export function App() {
  const [activeTab, setActiveTab] = useState<Tab>("chat")
  const settings = useLocalSettings()
  const disc = useDiscussions()
  const discRef = useRef(disc)
  discRef.current = disc

  useEffect(() => {
    const root = document.documentElement
    root.classList.toggle("dark", settings.theme === "dark")
    root.dataset.contrast = settings.contrast
  }, [settings.theme, settings.contrast])

  const wsUrl = useCallback(() => {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:"
    return `${protocol}//${window.location.host}/ws`
  }, [])

  const onReconnect = useCallback(() => {
    discRef.current.refreshDiscussions()
  }, [])

  return (
    <WsEventProvider url={wsUrl} onReconnect={onReconnect}>
      <WsDiscussionBridge discRef={discRef} />
      <AppShell activeTab={activeTab} onTabChange={setActiveTab}>
        <div className="h-full overflow-hidden">
          {activeTab === "chat" && <ChatView disc={disc} />}
          {activeTab === "heartbeats" && <HeartbeatsPanel />}
          {activeTab === "memory" && <MemoryPanel />}
        </div>
      </AppShell>
    </WsEventProvider>
  )
}
