import { useEffect, useState } from "react"
import type { VoiceSessionPhase } from "../hooks/use-voice-session"

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
  onMute: () => void
  onEnd: () => void
}

function formatElapsed(startedAt: number | null, now: number) {
  if (!startedAt) return "0:00"
  const seconds = Math.max(0, Math.floor((now - startedAt) / 1000))
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`
}

export function VoiceSessionPanel({ agentName, avatarUrl, phase, muted, startedAt, error,
  delegating, userCaption, assistantCaption, onMute, onEnd }: Props) {
  const [now, setNow] = useState(Date.now())
  useEffect(() => {
    if (!startedAt) return
    const interval = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(interval)
  }, [startedAt])

  const connected = phase === "connected"
  const status = phase === "starting" ? "Connecting…"
    : phase === "degraded" ? "Voice control interrupted"
      : phase === "error" ? "Could not connect"
        : phase === "closing" ? "Ending…"
          : connected ? "Live" : phase

  return (
    <div className="absolute inset-0 z-40 flex flex-col overflow-hidden bg-background" data-slot="voice-session-panel" data-state={phase}
      role="dialog" aria-modal="true" aria-label={`Voice conversation with ${agentName}`}>
      <div className="flex items-center justify-between px-4 py-3">
        <div className="flex items-center gap-2 text-xs text-text-muted">
          <span className={`size-2 rounded-full ${connected ? "bg-emerald-400 shadow-[0_0_12px_rgba(52,211,153,.7)]" : "bg-overlay-30"}`} />
          <span>{status}</span>
        </div>
        <span className="font-mono text-xs tabular-nums text-text-muted">{formatElapsed(startedAt, now)}</span>
      </div>

      <div className="flex min-h-0 flex-1 flex-col items-center justify-center px-6 pb-5 text-center">
        <div className={`relative mb-7 size-32 rounded-full border border-overlay-15 bg-overlay-6 p-1 transition-transform duration-500 ${assistantCaption ? "scale-105" : "scale-100"}`}>
          {avatarUrl ? <img src={avatarUrl} alt="" className="size-full rounded-full object-cover" />
            : <div className="flex size-full items-center justify-center rounded-full text-3xl text-accent"><i className="ph-bold ph-star" /></div>}
          {connected && <div className="pointer-events-none absolute -inset-3 rounded-full border border-accent-a20 animate-pulse" />}
        </div>
        <h2 className="mb-1 text-xl font-semibold text-contrast">{agentName}</h2>
        <p className="mb-8 text-xs text-text-muted">GPT-Live voice · Nova memory and tools</p>

        <div className="flex min-h-28 w-full max-w-lg flex-col justify-end gap-3" aria-live="polite">
          {assistantCaption && <p className="text-balance text-base leading-relaxed text-text-primary">{assistantCaption}</p>}
          {userCaption && <p className="text-balance text-sm leading-relaxed text-text-muted">{userCaption}</p>}
          {!assistantCaption && !userCaption && phase === "starting" && <p className="text-sm text-text-muted">Opening the live audio channel…</p>}
          {delegating && <p className="text-sm text-accent">I’m working on that…</p>}
          {error && <p className="rounded-lg border border-red-400/20 bg-red-400/5 px-3 py-2 text-sm text-red-300">{error}</p>}
        </div>
      </div>

      <div className="flex items-center justify-center gap-5 px-4 pb-[max(1.5rem,env(safe-area-inset-bottom))] pt-3">
        <button type="button" onClick={onMute} disabled={!connected}
          className={`flex size-14 items-center justify-center rounded-full border transition-colors disabled:opacity-30 ${muted ? "border-amber-400/40 bg-amber-400/15 text-amber-300" : "border-overlay-15 bg-overlay-8 text-contrast hover:bg-overlay-12"}`}
          aria-label={muted ? "Unmute microphone" : "Mute microphone"} title={muted ? "Unmute" : "Mute"}>
          <i className={`ph-bold ${muted ? "ph-microphone-slash" : "ph-microphone"} text-xl`} />
        </button>
        <button type="button" onClick={onEnd}
          className="flex size-16 items-center justify-center rounded-full bg-red-500 text-white shadow-lg shadow-red-500/20 transition-transform hover:scale-105 active:scale-95"
          aria-label="End voice session" title="End voice session">
          <i className="ph-bold ph-phone-disconnect text-2xl" />
        </button>
      </div>
    </div>
  )
}
