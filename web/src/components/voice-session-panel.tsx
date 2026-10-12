import { useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type RefObject } from "react"
import type { VoiceSessionPhase } from "../hooks/use-voice-session"
import {
  resolveVoiceSessionVisualState,
  voiceSessionStatusLabel,
} from "./voice-session-visual"

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
  returnFocusRef: RefObject<HTMLButtonElement | null>
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
  delegating, userCaption, assistantCaption, returnFocusRef, onMute, onEnd }: Props) {
  const [now, setNow] = useState(Date.now())
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
  const visualState = resolveVoiceSessionVisualState({ phase, muted, delegating, assistantActive, userActive })
  const status = voiceSessionStatusLabel(visualState)
  const caption = useMemo(() => {
    if (delegating) return { speaker: agentName, text: "I’m working on that…" }
    if (assistantCaption) return { speaker: agentName, text: assistantCaption }
    if (userCaption) return { speaker: "You", text: userCaption }
    if (phase === "starting") return { speaker: "", text: "Opening the live audio channel…" }
    if (connected) return { speaker: "", text: muted ? "Unmute when you’re ready." : "Go ahead. I’m listening." }
    return null
  }, [agentName, assistantCaption, connected, delegating, muted, phase, userCaption])

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "Escape") {
      event.preventDefault()
      onEnd()
      return
    }
    if (event.key !== "Tab") return
    const controls = [muteRef.current, endRef.current].filter(
      (control): control is HTMLButtonElement => Boolean(control && !control.disabled),
    )
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
      className="nova-voice-mode fixed inset-0 z-[80] isolate flex min-h-[100dvh] flex-col overflow-hidden bg-background text-contrast"
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
        <span className="nova-voice-mode__timer font-mono text-xs tabular-nums">{formatElapsed(startedAt, now)}</span>
      </header>

      <main className="relative z-10 flex min-h-0 flex-1 flex-col items-center justify-center px-5 py-5 text-center sm:px-8">
        <div className="nova-voice-mode__identity" aria-hidden="true">
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

        <h2 className="nova-voice-mode__name mt-7 text-2xl font-semibold tracking-tight sm:text-3xl">{agentName}</h2>
        <p className="nova-voice-mode__provenance mt-1.5 text-xs">GPT-Live · Nova memory and tools</p>

        <div className="nova-voice-mode__caption mt-7 flex min-h-24 w-full max-w-xl flex-col justify-center px-5 py-4" aria-live="polite" aria-atomic="true">
          {caption ? (
            <>
              {caption.speaker && <span className="mb-1.5 text-[10px] font-semibold uppercase tracking-[0.18em] text-white/55">{caption.speaker}</span>}
              <p className="text-balance text-[15px] leading-relaxed text-white/90 sm:text-base">{caption.text}</p>
            </>
          ) : <span className="text-sm text-white/55">The conversation is still here.</span>}
        </div>

        {error && (
          <p className="nova-voice-mode__error mt-3 max-w-xl rounded-xl border border-red-300/20 bg-red-950/35 px-4 py-2.5 text-sm text-red-100" role="alert">
            {error}
          </p>
        )}
      </main>

      <footer className="relative z-10 flex items-end justify-center gap-8 px-4 pb-[max(1.25rem,env(safe-area-inset-bottom))] pt-2 sm:pb-7">
        <div className="flex flex-col items-center gap-2">
          <button
            ref={muteRef}
            type="button"
            onClick={onMute}
            disabled={!connected}
            className="nova-voice-mode__control nova-voice-mode__control--mute"
            data-active={muted || undefined}
            aria-label={muted ? "Unmute microphone" : "Mute microphone"}
            title={muted ? "Unmute" : "Mute"}
          >
            <i className={`ph-bold ${muted ? "ph-microphone-slash" : "ph-microphone"} text-xl`} aria-hidden="true" />
          </button>
          <span className="text-[11px] font-medium text-white/65">{muted ? "Unmute" : "Mute"}</span>
        </div>
        <div className="flex flex-col items-center gap-2">
          <button
            ref={endRef}
            type="button"
            onClick={onEnd}
            className="nova-voice-mode__control nova-voice-mode__control--end"
            aria-label="End voice session"
            title="End voice session"
          >
            <i className="ph-fill ph-phone-disconnect text-2xl" aria-hidden="true" />
          </button>
          <span className="text-[11px] font-medium text-white/65">End</span>
        </div>
      </footer>
    </div>
  )
}
