import { useEffect, useCallback, useRef, createContext, useContext } from "react"
import { createBrowserRouter, RouterProvider, Outlet, useNavigate } from "react-router-dom"
import {
  WsEventProvider, useWsSubscribe, BreadcrumbLabelProvider, AuthProvider, useAuth,
  useAskNovaReceiver, usePendingNovaContext,
  type AskNovaContext, type PendingNovaContext,
} from "@redbamboo/utility"
import { AppShell } from "@/components/layout/app-shell"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { useDiscussions } from "@/hooks/use-discussions"
import type { WsEvent } from "@/lib/types"
import { routes } from "@/routes"

type DiscussionsHook = ReturnType<typeof useDiscussions>

interface AppContext {
  disc: DiscussionsHook
  pendingContext: PendingNovaContext
}

const AppContextValue = createContext<AppContext>(null!)

export function useDisc(): DiscussionsHook {
  return useContext(AppContextValue).disc
}

export function useNovaPendingContext(): PendingNovaContext {
  return useContext(AppContextValue).pendingContext
}

function WsDiscussionBridge({ discRef }: { discRef: React.RefObject<DiscussionsHook> }) {
  useWsSubscribe((event) => {
    discRef.current.handleWsEvent(event as WsEvent)
  })
  return null
}

function AskNovaHandler({ discRef, pendingContext }: { discRef: React.RefObject<DiscussionsHook>; pendingContext: PendingNovaContext }) {
  const navigate = useNavigate()

  const onContext = useCallback(async (ctx: AskNovaContext) => {
    const d = await discRef.current.createDiscussion()
    if (d) {
      pendingContext.set(ctx)
      navigate(`/chat/${d.id}`)
    }
  }, [navigate, pendingContext])

  useAskNovaReceiver({ onContext })
  return null
}

function AppLayout() {
  const settings = useLocalSettings()
  const { isLoading, isAuthenticated } = useAuth()
  const disc = useDiscussions()
  const discRef = useRef(disc)
  discRef.current = disc
  const pendingContext = usePendingNovaContext()

  useEffect(() => {
    if (!isLoading && !isAuthenticated) {
      window.location.href = "/login"
    }
  }, [isLoading, isAuthenticated])

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
    discRef.current.reloadActiveMessages(true)
  }, [])

  const onVisibilityChange = useCallback(() => {
    discRef.current.syncAndRefresh()
    discRef.current.reloadActiveMessages()
  }, [])

  if (isLoading || !isAuthenticated) return null

  const appCtx: AppContext = { disc, pendingContext }

  return (
    <WsEventProvider url={wsUrl} onReconnect={onReconnect} onVisibilityChange={onVisibilityChange}>
      <WsDiscussionBridge discRef={discRef} />
      <AskNovaHandler discRef={discRef} pendingContext={pendingContext} />
      <AppContextValue.Provider value={appCtx}>
        <BreadcrumbLabelProvider>
          <AppShell>
            <div className="h-full overflow-hidden">
              <Outlet />
            </div>
          </AppShell>
        </BreadcrumbLabelProvider>
      </AppContextValue.Provider>
    </WsEventProvider>
  )
}

const router = createBrowserRouter([
  {
    element: <AppLayout />,
    children: routes,
  },
])

export function App() {
  return (
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>
  )
}
