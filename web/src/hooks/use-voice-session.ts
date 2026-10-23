import { useCallback, useEffect, useRef, useState } from "react"
import { api } from "../lib/api"
import { getSettings } from "../lib/settings-store"
import { acquireVoiceInput, applyVoiceOutputDevice } from "../lib/voice-audio-devices"
import { applyVoiceEvent, type VoiceCaptionState } from "./voice-session-events"
import { useScreenWakeLock } from "./use-screen-wake-lock"

export type VoiceSessionPhase = "idle" | "ready" | "starting" | "connected" | "degraded" | "closing" | "closed" | "error"

const VOICE_MODE_EXIT_MS = 160

interface VoiceSessionAdmission {
  id: string
  provider: string
  model: string
  voice: string
  state: string
  answerSdp: string
}

function waitForIceGathering(pc: RTCPeerConnection): Promise<void> {
  if (pc.iceGatheringState === "complete") return Promise.resolve()
  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      cleanup()
      reject(new Error("Voice connection timed out while gathering network candidates"))
    }, 10_000)
    const onChange = () => {
      if (pc.iceGatheringState !== "complete") return
      cleanup()
      resolve()
    }
    const cleanup = () => {
      window.clearTimeout(timeout)
      pc.removeEventListener("icegatheringstatechange", onChange)
    }
    pc.addEventListener("icegatheringstatechange", onChange)
  })
}

export function useVoiceSession(discussionId: string | null) {
  const [phase, setPhase] = useState<VoiceSessionPhase>("idle")
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [muted, setMuted] = useState(false)
  const [startedAt, setStartedAt] = useState<number | null>(null)
  const [delegating, setDelegating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [captions, setCaptions] = useState<VoiceCaptionState>({ speaker: null, user: "", assistant: "" })
  const peerRef = useRef<RTCPeerConnection | null>(null)
  const channelRef = useRef<RTCDataChannel | null>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const sessionRef = useRef<string | null>(null)
  const discussionRef = useRef<string | null>(null)
  const closeTimerRef = useRef<number | null>(null)
  const startAttemptRef = useRef(0)
  const keepScreenAwake = phase === "starting" || phase === "connected" || phase === "degraded"
  useScreenWakeLock(keepScreenAwake)

  const releaseMedia = useCallback(() => {
    channelRef.current?.close()
    channelRef.current = null
    peerRef.current?.close()
    peerRef.current = null
    streamRef.current?.getTracks().forEach(track => track.stop())
    streamRef.current = null
    if (audioRef.current) {
      audioRef.current.pause()
      audioRef.current.srcObject = null
      audioRef.current = null
    }
  }, [])

  const finishCloseAfterTransition = useCallback(() => {
    if (closeTimerRef.current !== null) window.clearTimeout(closeTimerRef.current)
    closeTimerRef.current = window.setTimeout(() => {
      closeTimerRef.current = null
      sessionRef.current = null
      discussionRef.current = null
      setSessionId(null)
      setStartedAt(null)
      setMuted(false)
      setDelegating(false)
      setCaptions({ speaker: null, user: "", assistant: "" })
      setPhase("closed")
    }, VOICE_MODE_EXIT_MS)
  }, [])

  const open = useCallback(() => {
    if (!discussionId || (phase !== "idle" && phase !== "closed")) return
    if (closeTimerRef.current !== null) {
      window.clearTimeout(closeTimerRef.current)
      closeTimerRef.current = null
    }
    startAttemptRef.current++
    discussionRef.current = discussionId
    setSessionId(null)
    setStartedAt(null)
    setMuted(false)
    setDelegating(false)
    setError(null)
    setCaptions({ speaker: null, user: "", assistant: "" })
    setPhase("ready")
  }, [discussionId, phase])

  const stop = useCallback(async () => {
    if (phase === "idle" || phase === "closed" || phase === "closing") return
    startAttemptRef.current++
    const currentDiscussion = discussionRef.current
    const currentSession = sessionRef.current
    setPhase("closing")
    releaseMedia()
    finishCloseAfterTransition()
    if (currentDiscussion && currentSession) {
      try {
        await api.post(`/api/apps/nova/discussions/${encodeURIComponent(currentDiscussion)}/voice-sessions/${encodeURIComponent(currentSession)}/close`)
      } catch { /* transport is already closed locally */ }
    }
  }, [finishCloseAfterTransition, phase, releaseMedia])

  const start = useCallback(async () => {
    if (!discussionId || (phase !== "ready" && phase !== "error")) return
    if (closeTimerRef.current !== null) {
      window.clearTimeout(closeTimerRef.current)
      closeTimerRef.current = null
    }
    setPhase("starting")
    const attempt = ++startAttemptRef.current
    discussionRef.current = discussionId
    setError(null)
    setCaptions({ speaker: null, user: "", assistant: "" })
    try {
      if (!navigator.mediaDevices?.getUserMedia)
        throw new Error("This browser does not expose microphone access")
      const voiceSettings = getSettings()
      const input = await acquireVoiceInput(
        voiceSettings,
        audio => navigator.mediaDevices.getUserMedia({ audio }),
      )
      const stream = input.stream
      if (input.notice) setError(input.notice)
      streamRef.current = stream
      const pc = new RTCPeerConnection()
      peerRef.current = pc
      stream.getAudioTracks().forEach(track => pc.addTrack(track, stream))

      const audio = new Audio()
      audio.autoplay = true
      audioRef.current = audio
      pc.ontrack = event => {
        void (async () => {
          const outputNotice = await applyVoiceOutputDevice(audio, voiceSettings)
          if (audioRef.current !== audio) return
          if (outputNotice) setError(current => current ? `${current} ${outputNotice}` : outputNotice)
          audio.srcObject = event.streams[0] ?? new MediaStream([event.track])
          await audio.play().catch(() => setError("Tap the screen once to allow voice playback"))
        })()
      }
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === "connected") {
          setPhase("connected")
          setStartedAt(value => value ?? Date.now())
        } else if (pc.connectionState === "failed" || pc.connectionState === "disconnected") {
          setPhase("degraded")
        }
      }

      const channel = pc.createDataChannel("oai-events")
      channelRef.current = channel
      channel.onmessage = message => {
        try {
          const event = JSON.parse(String(message.data)) as Record<string, unknown>
          setCaptions(previous => applyVoiceEvent(previous, event))
          if (event.type === "session.delegation.created") setDelegating(true)
          else if (event.type === "session.output_transcript.delta") setDelegating(false)
          if (event.type === "session.started") {
            setPhase("connected")
            setStartedAt(value => value ?? Date.now())
          } else if (event.type === "session.closed") {
            setPhase("closing")
            releaseMedia()
            finishCloseAfterTransition()
          } else if (event.type === "error") {
            setPhase("degraded")
          }
        } catch { /* ignore unknown provider events */ }
      }

      const offer = await pc.createOffer()
      await pc.setLocalDescription(offer)
      await waitForIceGathering(pc)
      const offerSdp = pc.localDescription?.sdp
      if (!offerSdp) throw new Error("The browser did not produce a voice connection offer")
      const admission = await api.post<VoiceSessionAdmission>(
        `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/voice-sessions`,
        { offerSdp, provider: "openai-live" },
      )
      if (attempt !== startAttemptRef.current) {
        await api.post(`/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/voice-sessions/${encodeURIComponent(admission.id)}/close`).catch(() => undefined)
        return
      }
      sessionRef.current = admission.id
      discussionRef.current = discussionId
      setSessionId(admission.id)
      await pc.setRemoteDescription({ type: "answer", sdp: admission.answerSdp })
    } catch (caught) {
      releaseMedia()
      sessionRef.current = null
      setSessionId(null)
      setPhase("error")
      setError(caught instanceof Error ? caught.message : "Voice session could not start")
    }
  }, [discussionId, finishCloseAfterTransition, phase, releaseMedia])

  const toggleMute = useCallback(() => {
    const next = !muted
    streamRef.current?.getAudioTracks().forEach(track => { track.enabled = !next })
    setMuted(next)
  }, [muted])

  useEffect(() => {
    if (discussionRef.current && discussionRef.current !== discussionId) void stop()
  }, [discussionId, stop])
  useEffect(() => {
    const closeWhenBackgrounded = () => {
      if (document.visibilityState === "hidden") void stop()
    }
    const closeWhenPageLeaves = () => { void stop() }
    document.addEventListener("visibilitychange", closeWhenBackgrounded)
    window.addEventListener("pagehide", closeWhenPageLeaves)
    return () => {
      document.removeEventListener("visibilitychange", closeWhenBackgrounded)
      window.removeEventListener("pagehide", closeWhenPageLeaves)
    }
  }, [stop])
  useEffect(() => () => {
    startAttemptRef.current++
    if (closeTimerRef.current !== null) window.clearTimeout(closeTimerRef.current)
    const currentDiscussion = discussionRef.current
    const currentSession = sessionRef.current
    releaseMedia()
    if (currentDiscussion && currentSession) {
      void api.post(`/api/apps/nova/discussions/${encodeURIComponent(currentDiscussion)}/voice-sessions/${encodeURIComponent(currentSession)}/close`)
    }
  }, [releaseMedia])

  return { phase, sessionId, muted, startedAt, delegating, error, captions, open, start, stop, toggleMute }
}
