import { useRef, useMemo, createContext, useContext } from "react"
import { useRoutes, useLocation } from "react-router-dom"
import {
  useWsSubscribe,
  usePluginBreadcrumbs,
} from "@redbamboo/utility"
import { ToastProvider } from "@redbamboo/ui"
import { AppShell } from "./components/layout/app-shell"
import { useDiscussions } from "./hooks/use-discussions"
import { useEventTypes } from "./hooks/use-event-types"
import type { WsEvent } from "./lib/types"
import { routes } from "./routes"
import { usePendingVisibleContext, type PendingVisibleContextController } from "./hooks/use-pending-visible-context"

type DiscussionsHook = ReturnType<typeof useDiscussions>

interface AppContext {
  disc: DiscussionsHook
  pendingContext: PendingVisibleContextController
}

const AppContextValue = createContext<AppContext>(null!)

export function useDisc(): DiscussionsHook {
  return useContext(AppContextValue).disc
}

export function useNovaPendingContext(): PendingVisibleContextController {
  return useContext(AppContextValue).pendingContext
}

function WsDiscussionBridge({ discRef }: { discRef: React.RefObject<DiscussionsHook> }) {
  useWsSubscribe((event) => {
    if (event.type === "upstream.disconnected" || event.type === "websocket.disconnected") {
      discRef.current.handleUpstreamDisconnect()
    } else if (event.type === "upstream.connected" || event.type === "websocket.connected") {
      discRef.current.handleUpstreamReconnect()
    } else {
      if (event.type === "agent.avatar-changed") window.dispatchEvent(new Event("nova:avatar-changed"))
      discRef.current.handleWsEvent(event as WsEvent)
    }
  })
  return null
}

/**
 * Nova as a Leaf plugin page. The host shell owns auth, theme, the WebSocket
 * provider, and the command palette. This component mounts Nova's chat routes and
 * the discussion state shared across them.
 */
function NovaAppInner() {
  const element = useRoutes(routes)
  const { pathname } = useLocation()
  usePluginBreadcrumbs(routes, "/apps/nova", pathname)

  return (
    <NovaRuntimeProvider>
      <AppShell>
        <div className="h-full overflow-hidden">{element}</div>
      </AppShell>
    </NovaRuntimeProvider>
  )
}

export function NovaRuntimeProvider({
  children,
}: {
  children: React.ReactNode
}) {
  const { resolve: resolveEventType } = useEventTypes()
  const disc = useDiscussions(resolveEventType)
  const discRef = useRef(disc)
  discRef.current = disc
  const pendingContext = usePendingVisibleContext()

  const appCtx = useMemo<AppContext>(() => ({ disc, pendingContext }), [disc, pendingContext])
  return (
    <>
      <WsDiscussionBridge discRef={discRef} />
      <AppContextValue.Provider value={appCtx}>
        {children}
      </AppContextValue.Provider>
    </>
  )
}

export function NovaApp() {
  return (
    <ToastProvider>
      <NovaAppInner />
    </ToastProvider>
  )
}
