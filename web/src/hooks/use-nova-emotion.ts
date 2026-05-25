import { useState, useEffect, useRef, useCallback } from "react"
import type { MessageBlock } from "@redbamboo/chat"
import {
  type NovaEmotion,
  type EmotionRule,
  defaultRules,
  resolveEmotion,
  buildEmotionContext,
} from "@/lib/nova-emotion"

const DEBOUNCE_MS = 600
const MIN_HOLD_MS = 2000
const STATIC_FALLBACK = "/nova-avatar.png"

function emotionSrc(emotion: NovaEmotion): string {
  return `/nova-${emotion}.webp`
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
      img.src = emotionSrc(emotionName)
    })
  }, [])

  const applyEmotion = useCallback(async (next: NovaEmotion) => {
    if (next === emotion) return

    if (await checkAvailable(next)) {
      setEmotion(next)
      setSrc(emotionSrc(next))
      lastChangeRef.current = Date.now()
      return
    }
    if (next !== "idle" && await checkAvailable("idle")) {
      setEmotion("idle")
      setSrc(emotionSrc("idle"))
      lastChangeRef.current = Date.now()
      return
    }
    setEmotion("idle")
    setSrc(STATIC_FALLBACK)
    lastChangeRef.current = Date.now()
  }, [emotion, checkAvailable])

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

  return { emotion, src }
}
