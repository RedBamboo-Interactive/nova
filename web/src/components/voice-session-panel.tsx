import { useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type RefObject } from "react"
import {
  ChatPanel,
  type MessageBlock,
  type ParsedEvent,
  type TranscriptPayloadLoader,
  type TranscriptPayloadRef,
} from "@redbamboo/chat"
import { Button } from "@redbamboo/ui"
import type { VoiceSessionPhase } from "../hooks/use-voice-session"
import {
  resolveVoiceSessionVisualState,
  voiceSessionStatusLabel,
} from "./voice-session-visual"
import { projectVoiceCaption, type VoiceCaptionSpeaker } from "./voice-session-messages"

interface Props {
  agentName: string
  avatarUrl?: string | null
  phase: VoiceSessionPhase
  muted: boolean
  startedAt: number | null
  delegating: boolean
  error: string | null
  userCaption: string
  assistantCaption: string
  captionSpeaker: VoiceCaptionSpeaker
  messages: MessageBlock[]
  isStreaming: boolean
  resolveImageSrc?: (src: string) => string | undefined
  resolveFileLink?: (filePath: string, opts?: { line?: number }) => (() => void) | undefined
  resolveEventLink?: (event: ParsedEvent) => (() => void) | undefined
  loadTranscriptPayload?: TranscriptPayloadLoader
  getTranscriptPayloadMediaUrl?: (ref: TranscriptPayloadRef) => string
  getTranscriptPayloadDownloadUrl?: (ref: TranscriptPayloadRef) => string
  returnFocusRef: RefObject<HTMLButtonElement | null>
  onStart: () => void
  onMute: () => void
  onEnd: () => void
}

function formatElapsed(startedAt: number | null, now: number) {
  if (!startedAt) return "0:00"
  const seconds = Math.max(0, Math.floor((now - startedAt) / 1000))
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`
}

const ACTIVITY_TAIL_MS = 850

function useRecentActivity(value: string) {
  const [active, setActive] = useState(false)
  useEffect(() => {
    if (!value) {
      setActive(false)
      return
    }
    setActive(true)
    const timeout = window.setTimeout(() => setActive(false), ACTIVITY_TAIL_MS)
    return () => window.clearTimeout(timeout)
  }, [value])
  return active
}

export function VoiceSessionPanel({ agentName, avatarUrl, phase, muted, startedAt, error,
  delegating, userCaption, assistantCaption, captionSpeaker, messages, isStreaming, resolveImageSrc,
  resolveFileLink, resolveEventLink, loadTranscriptPayload, getTranscriptPayloadMediaUrl, getTranscriptPayloadDownloadUrl,
  returnFocusRef, onStart, onMute, onEnd }: Props) {
  const [now, setNow] = useState(Date.now())
  const panelRef = useRef<HTMLDivElement>(null)
  const muteRef = useRef<HTMLButtonElement>(null)
  const endRef = useRef<HTMLButtonElement>(null)
  const assistantActive = useRecentActivity(assistantCaption)
  const userActive = useRecentActivity(userCaption)

  useEffect(() => {
    if (!startedAt) return
    const interval = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(interval)
  }, [startedAt])

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    endRef.current?.focus({ preventScroll: true })
    return () => {
      const returnTarget = returnFocusRef.current ?? (previousFocus?.isConnected ? previousFocus : null)
      returnTarget?.focus({ preventScroll: true })
    }
  }, [returnFocusRef])

  const connected = phase === "connected"
  const canStart = phase === "ready" || phase === "error"
  const visualState = resolveVoiceSessionVisualState({ phase, muted, delegating, assistantActive, userActive })
  const status = voiceSessionStatusLabel(visualState)
  const captionTimestamp = useMemo(() => new Date().toISOString(), [captionSpeaker])
  const displayMessages = useMemo(() => projectVoiceCaption(messages, {
    speaker: captionSpeaker,
    userCaption,
    assistantCaption,
    timestamp: captionTimestamp,
  }), [assistantCaption, captionSpeaker, captionTimestamp, messages, userCaption])
  const assistiveCaption = captionSpeaker === "assistant" ? assistantCaption : captionSpeaker === "user" ? userCaption : ""

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // Tool and event details are portaled dialogs. Their React events still
    // bubble through this component even though their DOM lives outside the
    // voice panel; leave their own focus trap and Escape handling authoritative.
    if (!panelRef.current?.contains(event.target as Node)) return
    if (event.key === "Escape") {
      event.preventDefault()
      onEnd()
      return
    }
    if (event.key !== "Tab") return
    const controls = Array.from(panelRef.current?.querySelectorAll<HTMLElement>(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    ) ?? []).filter(control => control.getAttribute("aria-hidden") !== "true" && control.getClientRects().length > 0)
    if (controls.length === 0) return
    const currentIndex = controls.indexOf(document.activeElement as HTMLButtonElement)
    const nextIndex = event.shiftKey
      ? (currentIndex <= 0 ? controls.length - 1 : currentIndex - 1)
      : (currentIndex < 0 || currentIndex === controls.length - 1 ? 0 : currentIndex + 1)
    event.preventDefault()
    controls[nextIndex].focus()
  }

  return (
    <div
      ref={panelRef}
      className="nova-voice-mode fixed inset-0 z-[80] isolate flex min-h-[100dvh] flex-col overflow-hidden bg-background text-foreground"
      data-slot="voice-session-panel"
      data-state={phase}
      data-visual-state={visualState}
      role="dialog"
      aria-modal="true"
      aria-label={`Voice conversation with ${agentName}`}
      onKeyDown={handleKeyDown}
    >
      <div className="nova-voice-mode__backdrop" aria-hidden="true">
        {avatarUrl && <img src={avatarUrl} alt="" className="nova-voice-mode__backdrop-image" />}
        <div className="nova-voice-mode__backdrop-wash" />
        <div className="nova-voice-mode__backdrop-vignette" />
        <div className="nova-voice-mode__backdrop-grain" />
      </div>

      <header className="nova-voice-mode__header relative z-10 flex items-center justify-between gap-3 px-4 pt-[max(1rem,env(safe-area-inset-top))] sm:px-6 sm:pt-5">
        <div className="nova-voice-mode__status-pill" aria-live="polite">
          <span className="nova-voice-mode__status-dot" aria-hidden="true" />
          <span>{status}</span>
        </div>
        <div className="flex items-center gap-2">
          <span className="nova-voice-mode__timer font-mono text-xs tabular-nums">{formatElapsed(startedAt, now)}</span>
          {canStart && (
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              onClick={onEnd}
              aria-label="Close voice mode"
              title="Close voice mode"
            >
              <i className="ph-bold ph-x text-base" aria-hidden="true" />
            </Button>
          )}
        </div>
      </header>

      <main className="relative z-10 flex min-h-0 flex-1 flex-col items-center overflow-hidden px-5 py-4 text-center sm:px-8 sm:py-5">
        <div className="nova-voice-mode__content flex min-h-0 w-full max-w-3xl flex-1 flex-col items-center">
          <div className="nova-voice-mode__identity shrink-0" aria-hidden="true">
            <span className="nova-voice-mode__ring nova-voice-mode__ring--outer" />
            <span className="nova-voice-mode__ring nova-voice-mode__ring--middle" />
            <div className="nova-voice-mode__portrait-shell">
              {avatarUrl
                ? <img src={avatarUrl} alt="" className="nova-voice-mode__portrait" />
                : <div className="nova-voice-mode__portrait nova-voice-mode__portrait--fallback"><i className="ph-bold ph-star" /></div>}
              <div className="nova-voice-mode__meter">
                {[0, 1, 2, 3, 4].map(bar => <span key={bar} style={{ "--voice-bar": bar } as CSSProperties} />)}
              </div>
            </div>
          </div>

          <h2 className="nova-voice-mode__name mt-5 shrink-0 text-2xl font-semibold tracking-tight sm:text-3xl">{agentName}</h2>

          <p className="sr-only" aria-live="polite" aria-atomic="true">{assistiveCaption}</p>

          <section
            className="nova-voice-mode__discussion mt-1 flex min-h-0 w-full flex-1 flex-col overflow-hidden text-left"
            aria-label="Live discussion"
            data-slot="voice-session-discussion"
          >
            <div className="nova-voice-mode__discussion-body min-h-0 flex-1">
              {displayMessages.length > 0 ? (
                <ChatPanel
                  className="nova-voice-mode__chat-panel"
                  messages={displayMessages}
                  isStreaming={isStreaming}
                  sessionId={null}
                  persistQueue={false}
                  hideComposer
                  disabled
                  resolveImageSrc={resolveImageSrc}
                  resolveFileLink={resolveFileLink}
                  resolveEventLink={resolveEventLink}
                  loadTranscriptPayload={loadTranscriptPayload}
                  getTranscriptPayloadMediaUrl={getTranscriptPayloadMediaUrl}
                  getTranscriptPayloadDownloadUrl={getTranscriptPayloadDownloadUrl}
                  assistantAvatar={avatarUrl ?? undefined}
                />
              ) : (
                <div className="flex h-full items-center justify-center px-5 text-center text-xs text-muted-foreground">
                  The real discussion will appear here as the voice session delegates into Nova.
                </div>
              )}
            </div>
          </section>

          {error && (
            <p className="nova-voice-mode__error mt-3 max-w-xl shrink-0 rounded-xl border border-destructive-a30 bg-destructive-a10 px-4 py-2.5 text-sm text-destructive" role="alert">
              {error}
            </p>
          )}
        </div>
      </main>

      <footer className="relative z-10 flex items-end justify-center gap-8 px-4 pb-[max(1.25rem,env(safe-area-inset-bottom))] pt-2 sm:pb-7">
        <div className="flex flex-col items-center gap-2">
          <Button
            ref={muteRef}
            type="button"
            variant="outline"
            size="icon-lg"
            onClick={onMute}
            disabled={!connected}
            className="nova-voice-mode__control nova-voice-mode__control--mute rounded-full"
            data-active={muted || undefined}
            aria-label={muted ? "Unmute microphone" : "Mute microphone"}
            title={muted ? "Unmute" : "Mute"}
          >
            <i className={`ph-bold ${muted ? "ph-microphone-slash" : "ph-microphone"} text-xl`} aria-hidden="true" />
          </Button>
          <span className="text-[11px] font-medium text-muted-foreground">{muted ? "Unmute" : "Mute"}</span>
        </div>
        <div className="flex flex-col items-center gap-2">
          <Button
            ref={endRef}
            type="button"
            variant={canStart ? "default" : "destructive"}
            size="icon-lg"
            onClick={canStart ? onStart : onEnd}
            className={`nova-voice-mode__control ${canStart ? "nova-voice-mode__control--call" : "nova-voice-mode__control--end"} rounded-full`}
            aria-label={canStart ? "Start voice call" : "End voice session"}
            title={canStart ? "Start voice call" : "End voice session"}
          >
            <i className={`ph-bold ${canStart ? "ph-phone" : "ph-phone-disconnect"} text-2xl`} aria-hidden="true" />
          </Button>
          <span className="text-[11px] font-medium text-muted-foreground">{canStart ? "Call" : "End"}</span>
        </div>
      </footer>
    </div>
  )
}
