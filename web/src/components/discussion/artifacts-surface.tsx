import { useCallback, useEffect, useMemo, useRef } from "react"
import { Button, PanelHeader, Sheet, useDockPanel } from "@redbamboo/ui"
import { useWsSubscribe } from "@redbamboo/utility"
import { ArtifactsPanel, useArtifacts, type ArtifactsPanelProps } from "@redbamboo/chat"
import { api } from "../../lib/api"
import { NOVA_HIDDEN_ARTIFACT_ORIGINS, createNovaArtifactsLoader, resolveNovaArtifactTarget } from "../../lib/artifacts-api"

export const ARTIFACTS_DOCK_ID = "nova-artifacts"

/** The shell's own tool panels (Inspector, History) open at this share of the dock frame. */
const TOOL_PANEL_SHARE = "26.3259%"

/** Refreshes settle for this long so a burst of events is one read. */
const REFRESH_DEBOUNCE_MS = 500

interface ArtifactsSurfaceProps {
  discussionId: string | null
  open: boolean
  onClose: () => void
  /** True while a turn of the open discussion is running; its end is the settle signal. */
  isStreaming: boolean
  resolveImageSrc: (src: string) => string | undefined
  resolveFileLink: ArtifactsPanelProps["resolveFileLink"]
  onJumpToMessage: (messageUid: string) => void
}

/**
 * The Artifacts panel for the open discussion. Docked beside the chat when the shell docks,
 * otherwise a full-height sheet over it. Both are overlays beside the chat's own tree (a portal
 * into the dock host, a portal into the shell), so the chat never remounts when the panel opens,
 * closes, moves or switches between the two.
 */
export function ArtifactsSurface({
  discussionId,
  open,
  onClose,
  isStreaming,
  resolveImageSrc,
  resolveFileLink,
  onJumpToMessage,
}: ArtifactsSurfaceProps) {
  const load = useMemo(
    () => createNovaArtifactsLoader(discussionId ?? "", api.get),
    [discussionId],
  )
  const state = useArtifacts({ conversationKey: discussionId, enabled: open && !!discussionId, load })

  // Refresh when the conversation says something changed, never per stream token.
  const refreshRef = useRef(state.refresh)
  refreshRef.current = state.refresh
  const openRef = useRef(open)
  openRef.current = open
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const scheduleRefresh = useCallback(() => {
    if (!openRef.current) return
    if (timerRef.current !== null) clearTimeout(timerRef.current)
    timerRef.current = setTimeout(() => {
      timerRef.current = null
      if (openRef.current) void refreshRef.current()
    }, REFRESH_DEBOUNCE_MS)
  }, [])
  useEffect(() => () => {
    if (timerRef.current !== null) clearTimeout(timerRef.current)
  }, [])
  useEffect(() => {
    if (!open && timerRef.current !== null) {
      clearTimeout(timerRef.current)
      timerRef.current = null
    }
  }, [open])

  const wasStreaming = useRef(isStreaming)
  useEffect(() => {
    if (wasStreaming.current && !isStreaming) scheduleRefresh()
    wasStreaming.current = isStreaming
  }, [isStreaming, scheduleRefresh])

  useWsSubscribe(event => {
    if (event.type === "websocket.connected" || event.type === "upstream.connected") {
      scheduleRefresh()
      return
    }
    if (event.type !== "discussion.event" && event.type !== "discussion.nova-message") return
    const eventDiscussion = (event.data as { discussionId?: string } | undefined)?.discussionId
    if (eventDiscussion && eventDiscussion === discussionId) scheduleRefresh()
  })

  const docked = useRef(false)
  const jump = useCallback((messageUid: string) => {
    // Over the chat the sheet would hide the message it is about to show.
    if (!docked.current) onClose()
    onJumpToMessage(messageUid)
  }, [onClose, onJumpToMessage])

  const body = (inSheet: boolean) => (
    <div data-slot="artifacts-panel-root" className="flex h-full min-h-0 flex-col">
      <PanelHeader title="Artifacts" leading={<i aria-hidden="true" className="ph-bold ph-squares-four text-sm text-text-muted" />}>
        {!inSheet && (
          <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close Artifacts">
            <i aria-hidden="true" className="ph-bold ph-x" />
          </Button>
        )}
      </PanelHeader>
      <ArtifactsPanel
        className="min-h-0 flex-1"
        state={state}
        onRetry={state.reload}
        hiddenOrigins={NOVA_HIDDEN_ARTIFACT_ORIGINS}
        resolveTarget={resolveNovaArtifactTarget}
        resolveImageSrc={resolveImageSrc}
        resolveFileLink={resolveFileLink}
        onJumpToMessage={jump}
      />
    </div>
  )

  const dock = useDockPanel(ARTIFACTS_DOCK_ID, {
    title: "Artifacts",
    icon: "ph-bold ph-squares-four",
    defaultZone: "right",
    order: 15,
    defaultSize: TOOL_PANEL_SHARE,
    minSize: 260,
    open,
    onClose,
    content: body(false),
  })
  docked.current = dock.docked

  return (
    <>
      {dock.element}
      <Sheet
        open={open && !dock.docked}
        onOpenChange={next => { if (!next) onClose() }}
        aria-label="Artifacts"
        // Below the media viewer (z-59/60): a sheet above it would hide the image it just opened.
        className="top-0 z-[58] h-[100svh] max-h-[100svh] rounded-none border-t-0"
        overlayClassName="z-[57]"
        contentClassName="flex flex-col overflow-hidden"
        closeLabel="Close Artifacts"
      >
        {body(true)}
      </Sheet>
    </>
  )
}
