import type { VoiceSessionPhase } from "../hooks/use-voice-session"

export type VoiceSessionVisualState =
  | "ready"
  | "connecting"
  | "listening"
  | "speaking"
  | "working"
  | "muted"
  | "interrupted"
  | "error"
  | "ending"

interface VoiceSessionVisualInput {
  phase: VoiceSessionPhase
  muted: boolean
  delegating: boolean
  assistantActive: boolean
  userActive: boolean
}

export function resolveVoiceSessionVisualState({
  phase,
  muted,
  delegating,
  assistantActive,
  userActive,
}: VoiceSessionVisualInput): VoiceSessionVisualState {
  if (phase === "ready" || phase === "idle") return "ready"
  if (phase === "closing" || phase === "closed") return "ending"
  if (phase === "error") return "error"
  if (phase === "degraded") return "interrupted"
  if (phase === "starting") return "connecting"
  if (delegating) return "working"
  if (muted) return "muted"
  if (assistantActive) return "speaking"
  if (userActive) return "listening"
  return "listening"
}

export function voiceSessionStatusLabel(state: VoiceSessionVisualState): string {
  switch (state) {
    case "ready": return "Ready to call"
    case "connecting": return "Connecting"
    case "listening": return "Listening"
    case "speaking": return "Speaking"
    case "working": return "Working on that"
    case "muted": return "Microphone muted"
    case "interrupted": return "Connection interrupted"
    case "error": return "Could not connect"
    case "ending": return "Ending"
  }
}
