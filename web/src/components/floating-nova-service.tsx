import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { createPortal } from "react-dom"
import { useNavigate } from "react-router-dom"
import { dispatchGlobalPushToTalk, usePushToTalkSettings } from "@redbamboo/chat"
import { ToastProvider, UiEnvironmentProvider } from "@redbamboo/ui"
import {
  notifyUiSurfaceChanged,
  registerUiSurface,
  ShellLayerOutlet,
  useWsSubscribeByType,
  type UiSurfaceActionResult,
  type UiSurfaceSnapshot,
  type UiSurfaceState,
} from "@redbamboo/utility"
import { NovaRuntimeProvider, useDisc } from "../App"
import { ChatView } from "../panels/ChatView"
import { NewDiscussionPicker } from "./new-discussion-picker"
import {
  getFloatingNovaSupport,
  type FloatingNovaWindow,
} from "./floating-nova-support"
import { FLOATING_NOVA_NAVIGATION_EVENT, type FloatingNovaNavigationAction } from "../lib/floating-navigation"
import {
  GLOBAL_INPUT_EVENT_TYPE,
  GLOBAL_INPUT_LEASE_ENDPOINT,
  parseGlobalInputEvent,
  type GlobalInputLease,
  type GlobalInputLeaseState,
} from "../lib/global-input"
import { api } from "../lib/api"

export const FLOATING_NOVA_SURFACE_ID = "nova:floating-chat"
export const FLOATING_NOVA_COMMAND_ID = "nova:float-chat"
export const FLOATING_NOVA_SHORTCUT = "Ctrl+Alt+N"

const SELECTED_DISCUSSION_KEY = "nova-floating:selected-discussion"
const TRIGGER_SELECTOR = '[data-ui-surface="nova:floating-chat"][data-ui-action="open"]'

function copyDocumentPresentation(target: Document): () => void {
  target.title = "Nova"
  const viewport = target.createElement("meta")
  viewport.name = "viewport"
  viewport.content = "width=device-width, initial-scale=1"
  target.head.appendChild(viewport)

  for (const source of document.querySelectorAll<HTMLLinkElement | HTMLStyleElement>('link[rel="stylesheet"], style')) {
    const clone = source.cloneNode(true) as HTMLLinkElement | HTMLStyleElement
    if (source instanceof HTMLLinkElement && clone instanceof HTMLLinkElement) clone.href = source.href
    target.head.appendChild(clone)
  }

  const syncRoot = () => {
    target.documentElement.className = document.documentElement.className
    target.documentElement.setAttribute("style", document.documentElement.getAttribute("style") ?? "")
    target.documentElement.lang = document.documentElement.lang
    target.documentElement.dir = document.documentElement.dir
    for (const attribute of Array.from(target.documentElement.attributes)) {
      if (attribute.name.startsWith("data-") && !document.documentElement.hasAttribute(attribute.name))
        target.documentElement.removeAttribute(attribute.name)
    }
    for (const attribute of Array.from(document.documentElement.attributes)) {
      if (attribute.name.startsWith("data-")) target.documentElement.setAttribute(attribute.name, attribute.value)
    }
    target.documentElement.style.height = "100%"
  }
  syncRoot()
  target.body.className = "h-full min-h-0 overflow-hidden bg-background text-foreground antialiased"
  target.body.style.height = "100%"
  target.body.style.margin = "0"

  const observer = new MutationObserver(syncRoot)
  observer.observe(document.documentElement, { attributes: true })
  return () => observer.disconnect()
}

function FloatingNovaContent({
  selectedDiscussionId,
  globalInputState,
  pushToTalkKey,
  onSelectedDiscussionChange,
  onDock,
}: {
  selectedDiscussionId: string | null
  globalInputState: GlobalInputLeaseState
  pushToTalkKey: string
  onSelectedDiscussionChange: (id: string) => void
  onDock: () => void
}) {
  const { createDiscussion } = useDisc()
  const [pickerOpen, setPickerOpen] = useState(false)

  const handleCreate = useCallback(async (agentId: string, qualityTier: string, provider?: string) => {
    const discussion = await createDiscussion(agentId, qualityTier, provider)
    if (!discussion) return
    setPickerOpen(false)
    onSelectedDiscussionChange(discussion.id)
  }, [createDiscussion, onSelectedDiscussionChange])

  return (
    <div
      className="relative isolate h-full min-h-0 overflow-hidden bg-background text-foreground"
      data-global-push-to-talk={globalInputState}
      data-push-to-talk-key={pushToTalkKey}
    >
      <ShellLayerOutlet position="background" targetAppId="nova" />
      <ChatView
        presentation="floating"
        selectedDiscussionId={selectedDiscussionId}
        onSelectDiscussion={onSelectedDiscussionChange}
        onNewDiscussion={() => setPickerOpen(true)}
        onDock={onDock}
      />
      <NewDiscussionPicker
        open={pickerOpen}
        onClose={() => setPickerOpen(false)}
        onSelect={handleCreate}
      />
    </div>
  )
}

export function FloatingNovaService() {
  const navigate = useNavigate()
  const support = useMemo(getFloatingNovaSupport, [])
  const { key: pushToTalkKey } = usePushToTalkSettings()
  const [surfaceState, setSurfaceState] = useState<UiSurfaceState>(support.supported ? "closed" : "unsupported")
  const [pipWindow, setPipWindow] = useState<Window | null>(null)
  const [portalRoot, setPortalRoot] = useState<HTMLElement | null>(null)
  const [globalInputState, setGlobalInputState] = useState<GlobalInputLeaseState>("inactive")
  const [selectedDiscussionId, setSelectedDiscussionId] = useState<string | null>(() =>
    localStorage.getItem(SELECTED_DISCUSSION_KEY),
  )
  const stateRef = useRef(surfaceState)
  const windowRef = useRef<Window | null>(null)
  const selectedRef = useRef(selectedDiscussionId)
  const openingPromiseRef = useRef<Promise<UiSurfaceActionResult> | null>(null)
  const presentationCleanupRef = useRef<(() => void) | null>(null)
  const globalInputStateRef = useRef(globalInputState)
  const globalInputLeaseRef = useRef<GlobalInputLease | null>(null)

  stateRef.current = surfaceState
  windowRef.current = pipWindow
  selectedRef.current = selectedDiscussionId
  globalInputStateRef.current = globalInputState

  useWsSubscribeByType(GLOBAL_INPUT_EVENT_TYPE, (data) => {
    const event = parseGlobalInputEvent(data)
    const lease = globalInputLeaseRef.current
    const target = windowRef.current?.document
    if (!event || !lease || !target) return
    if (event.key !== pushToTalkKey || !event.leaseIds.includes(lease.leaseId)) return
    dispatchGlobalPushToTalk(target, { key: event.key, pressed: event.pressed })
  })

  useEffect(() => {
    if (!pipWindow) {
      globalInputStateRef.current = "inactive"
      setGlobalInputState("inactive")
      return
    }

    let cancelled = false
    let renewTimer: ReturnType<typeof setTimeout> | undefined
    const targetDocument = pipWindow.document

    const setLeaseState = (state: GlobalInputLeaseState) => {
      if (cancelled) return
      globalInputStateRef.current = state
      setGlobalInputState(state)
      notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)
    }

    const releaseLease = (lease: GlobalInputLease | null) => {
      dispatchGlobalPushToTalk(targetDocument, { key: pushToTalkKey, pressed: false })
      if (lease) void api.delete<{ released: boolean }>(`${GLOBAL_INPUT_LEASE_ENDPOINT}/${lease.leaseId}`).catch(() => {})
    }

    const acquireLease = async () => {
      setLeaseState("connecting")
      try {
        const lease = await api.post<GlobalInputLease>(GLOBAL_INPUT_LEASE_ENDPOINT, {
          feature: "push-to-talk",
          key: pushToTalkKey,
          surfaceId: FLOATING_NOVA_SURFACE_ID,
        })
        if (cancelled || pipWindow.closed) {
          releaseLease(lease)
          return
        }
        globalInputLeaseRef.current = lease
        setLeaseState("active")
        renewTimer = setTimeout(renewLease, lease.renewAfterMs)
      } catch {
        setLeaseState("unavailable")
      }
    }

    const renewLease = async () => {
      const lease = globalInputLeaseRef.current
      if (cancelled || !lease) return
      try {
        const renewed = await api.put<GlobalInputLease>(`${GLOBAL_INPUT_LEASE_ENDPOINT}/${lease.leaseId}`)
        if (cancelled || pipWindow.closed) {
          releaseLease(renewed)
          return
        }
        globalInputLeaseRef.current = renewed
        renewTimer = setTimeout(renewLease, renewed.renewAfterMs)
      } catch {
        globalInputLeaseRef.current = null
        releaseLease(lease)
        setLeaseState("unavailable")
      }
    }

    void acquireLease()
    return () => {
      cancelled = true
      if (renewTimer) clearTimeout(renewTimer)
      const lease = globalInputLeaseRef.current
      globalInputLeaseRef.current = null
      releaseLease(lease)
    }
  }, [pipWindow, pushToTalkKey])

  const updateSelectedDiscussion = useCallback((id: string) => {
    selectedRef.current = id
    setSelectedDiscussionId(id)
    localStorage.setItem(SELECTED_DISCUSSION_KEY, id)
    notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)
  }, [])

  const closeSurface = useCallback(() => {
    const current = windowRef.current
    if (!current || current.closed) return
    stateRef.current = "closing"
    setSurfaceState("closing")
    current.close()
  }, [])

  const dockSurface = useCallback(() => {
    const id = selectedRef.current
    closeSurface()
    navigate(id ? `/apps/nova/chat/${id}` : "/apps/nova/chat")
  }, [closeSurface, navigate])

  const openSurface = useCallback((discussionId?: string): Promise<UiSurfaceActionResult> => {
    if (discussionId) updateSelectedDiscussion(discussionId)
    if (!support.supported) {
      return Promise.resolve({
        ok: false,
        state: "unsupported",
        error: { code: support.reason ?? "unsupported", message: "Float Nova is available only in supported desktop browsers." },
      })
    }

    const existing = windowRef.current
    if (existing && !existing.closed) {
      existing.focus()
      return Promise.resolve({ ok: true, state: "open" })
    }
    if (openingPromiseRef.current) return openingPromiseRef.current

    if (!navigator.userActivation?.isActive) {
      return Promise.resolve({
        ok: false,
        state: stateRef.current,
        error: {
          code: "user_activation_required",
          message: "Opening Document Picture-in-Picture requires a trusted click or keyboard action.",
          selector: TRIGGER_SELECTOR,
          commandId: FLOATING_NOVA_COMMAND_ID,
          shortcut: FLOATING_NOVA_SHORTCUT,
        },
      })
    }

    const controller = (window as Window & FloatingNovaWindow).documentPictureInPicture!
    stateRef.current = "opening"
    setSurfaceState("opening")
    notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)

    // This call must remain in the trusted activation task. Do not await setup first.
    const opening = controller.requestWindow({
      width: 420,
      height: 700,
      preferInitialWindowPlacement: false,
    }).then((openedWindow) => {
      presentationCleanupRef.current?.()
      presentationCleanupRef.current = copyDocumentPresentation(openedWindow.document)
      const root = openedWindow.document.createElement("div")
      root.id = "nova-floating-root"
      root.style.height = "100%"
      openedWindow.document.body.replaceChildren(root)

      const handleClosed = () => {
        presentationCleanupRef.current?.()
        presentationCleanupRef.current = null
        windowRef.current = null
        stateRef.current = "closed"
        setPortalRoot(null)
        setPipWindow(null)
        setSurfaceState("closed")
        notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)
      }
      openedWindow.addEventListener("pagehide", handleClosed, { once: true })

      windowRef.current = openedWindow
      stateRef.current = "open"
      setPipWindow(openedWindow)
      setPortalRoot(root)
      setSurfaceState("open")
      notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)
      return { ok: true, state: "open" as const }
    }).catch((error: unknown) => {
      stateRef.current = "error"
      setSurfaceState("error")
      notifyUiSurfaceChanged(FLOATING_NOVA_SURFACE_ID)
      return {
        ok: false,
        state: "error" as const,
        error: {
          code: error instanceof DOMException ? error.name : "open_failed",
          message: error instanceof Error ? error.message : String(error),
        },
      }
    }).finally(() => {
      if (openingPromiseRef.current === opening) openingPromiseRef.current = null
    })
    openingPromiseRef.current = opening
    return opening
  }, [support, updateSelectedDiscussion])

  useEffect(() => {
    const registration = {
      getSnapshot: (): UiSurfaceSnapshot => ({
        id: FLOATING_NOVA_SURFACE_ID,
        owner: "nova",
        name: "Float Nova",
        description: "Compact Nova discussions and chat in a desktop always-on-top window.",
        kind: "document-picture-in-picture",
        supported: support.supported,
        unavailableReason: support.reason,
        state: stateRef.current,
        requiresUserActivation: true,
        commandId: FLOATING_NOVA_COMMAND_ID,
        shortcut: FLOATING_NOVA_SHORTCUT,
        selector: TRIGGER_SELECTOR,
        actions: ["open", "focus", "close", "dock", "select-discussion", "show-discussions", "show-chat", "next-discussion", "previous-discussion", "new-discussion"],
        selectedResource: selectedRef.current ? { type: "discussion", id: selectedRef.current } : null,
        inputCapabilities: [{
          id: "push-to-talk",
          kind: "keyboard-hold",
          scope: "global",
          key: pushToTalkKey,
          state: globalInputStateRef.current,
          leaseEndpoint: GLOBAL_INPUT_LEASE_ENDPOINT,
          eventType: GLOBAL_INPUT_EVENT_TYPE,
        }],
      }),
      runAction: (action: string, args?: Readonly<Record<string, unknown>>): Promise<UiSurfaceActionResult> | UiSurfaceActionResult => {
        const discussionId = typeof args?.discussionId === "string" ? args.discussionId : undefined
        if (action === "open") return openSurface(discussionId)
        if (action === "focus") {
          const current = windowRef.current
          if (!current || current.closed) return { ok: false, state: stateRef.current, error: { code: "surface_closed", message: "Float Nova is not open." } }
          current.focus()
          return { ok: true, state: "open" }
        }
        if (action === "close") {
          const current = windowRef.current
          closeSurface()
          return { ok: true, state: current && !current.closed ? "closing" : "closed" }
        }
        if (action === "dock") {
          dockSurface()
          return { ok: true, state: "closing" }
        }
        if (action === "select-discussion" && discussionId) {
          updateSelectedDiscussion(discussionId)
          return { ok: true, state: stateRef.current }
        }
        if (["show-discussions", "show-chat", "next-discussion", "previous-discussion", "new-discussion"].includes(action)) {
          const current = windowRef.current
          if (!current || current.closed) return { ok: false, state: stateRef.current, error: { code: "surface_closed", message: "Float Nova is not open." } }
          const navigationEvent = current.document.createEvent("CustomEvent")
          navigationEvent.initCustomEvent(
            FLOATING_NOVA_NAVIGATION_EVENT,
            false,
            false,
            { action: action as FloatingNovaNavigationAction },
          )
          current.document.dispatchEvent(navigationEvent)
          return { ok: true, state: "open" }
        }
        return { ok: false, state: stateRef.current, error: { code: "unsupported_action", message: `Unsupported Float Nova action '${action}'.` } }
      },
    }
    return registerUiSurface(FLOATING_NOVA_SURFACE_ID, registration)
  }, [closeSurface, dockSurface, openSurface, pushToTalkKey, support, updateSelectedDiscussion])

  useEffect(() => () => {
    presentationCleanupRef.current?.()
    const current = windowRef.current
    if (current && !current.closed) current.close()
  }, [])

  if (!pipWindow || !portalRoot) return null

  return createPortal(
    <UiEnvironmentProvider document={pipWindow.document} portalContainer={pipWindow.document.body}>
      <ToastProvider>
        <NovaRuntimeProvider>
          <FloatingNovaContent
            selectedDiscussionId={selectedDiscussionId}
            globalInputState={globalInputState}
            pushToTalkKey={pushToTalkKey}
            onSelectedDiscussionChange={updateSelectedDiscussion}
            onDock={dockSurface}
          />
        </NovaRuntimeProvider>
      </ToastProvider>
    </UiEnvironmentProvider>,
    portalRoot,
  )
}
