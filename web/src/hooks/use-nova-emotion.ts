import { useState, useEffect, useRef, useCallback } from "react"
import type { MessageBlock } from "@redbamboo/chat"
import {
  type NovaEmotion,
  type EmotionRule,
  defaultRules,
  resolveEmotion,
  emotionDuration,
  buildEmotionContext,
} from "@/lib/nova-emotion"

const DEBOUNCE_MS = 600
const MIN_HOLD_MS = 2000
const STATIC_FALLBACK = "/nova-avatar.png"

function emotionSrc(emotion: NovaEmotion, bust?: number): string {
  return bust ? `/nova-${emotion}.webp?t=${bust}` : `/nova-${emotion}.webp`
}

export function useNovaEmotion(
  messages: MessageBlock[],
  isStreaming: boolean,
  rules: EmotionRule[] = defaultRules,
) {
  const [emotion, setEmotion] = useState<NovaEmotion>("idle")
  const [src, setSrc] = useState(STATIC_FALLBACK)
  const availableRef = useRef<Set<string>>(new Set())
  const lastChangeRef = useRef(0)
  const pendingRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const durationRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const checkAvailable = useCallback((emotionName: NovaEmotion): Promise<boolean> => {
    if (availableRef.current.has(emotionName)) return Promise.resolve(true)
    if (availableRef.current.has(`!${emotionName}`)) return Promise.resolve(false)

    return new Promise((resolve) => {
      const img = new Image()
      img.onload = () => {
        availableRef.current.add(emotionName)
        resolve(true)
      }
      img.onerror = () => {
        availableRef.current.add(`!${emotionName}`)
        resolve(false)
      }
      img.src = `/nova-${emotionName}.webp`
    })
  }, [])

  const applyEmotion = useCallback(async (next: NovaEmotion, force?: boolean) => {
    if (next === emotion && !force) return

    if (durationRef.current) {
      clearTimeout(durationRef.current)
      durationRef.current = null
    }

    const bust = Date.now()

    if (await checkAvailable(next)) {
      setEmotion(next)
      setSrc(emotionSrc(next, bust))
      lastChangeRef.current = Date.now()
    } else if (next !== "idle" && await checkAvailable("idle")) {
      setEmotion("idle")
      setSrc(emotionSrc("idle"))
      lastChangeRef.current = Date.now()
      return
    } else {
      setEmotion("idle")
      setSrc(STATIC_FALLBACK)
      lastChangeRef.current = Date.now()
      return
    }

    const dur = emotionDuration(rules, next)
    if (dur) {
      durationRef.current = setTimeout(async () => {
        durationRef.current = null
        if (await checkAvailable("idle")) {
          setEmotion("idle")
          setSrc(emotionSrc("idle"))
        } else {
          setEmotion("idle")
          setSrc(STATIC_FALLBACK)
        }
        lastChangeRef.current = Date.now()
      }, dur)
    }
  }, [emotion, checkAvailable, rules])

  useEffect(() => {
    if (src === STATIC_FALLBACK) applyEmotion("idle", true)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const ctx = buildEmotionContext(messages, isStreaming)
    const next = resolveEmotion(rules, ctx)

    if (next === emotion) {
      if (pendingRef.current) {
        clearTimeout(pendingRef.current)
        pendingRef.current = null
      }
      return
    }

    const elapsed = Date.now() - lastChangeRef.current
    const wait = Math.max(DEBOUNCE_MS, MIN_HOLD_MS - elapsed)

    if (pendingRef.current) clearTimeout(pendingRef.current)
    pendingRef.current = setTimeout(() => {
      pendingRef.current = null
      applyEmotion(next)
    }, wait)

    return () => {
      if (pendingRef.current) {
        clearTimeout(pendingRef.current)
        pendingRef.current = null
      }
    }
  }, [messages, isStreaming, rules, emotion, applyEmotion])

  useEffect(() => {
    return () => {
      if (durationRef.current) clearTimeout(durationRef.current)
    }
  }, [])

  return { emotion, src }
}
