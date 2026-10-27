import { useState, useEffect, useCallback, useMemo, useRef } from "react"
import { useWsSubscribe } from "@redbamboo/utility"
import { useToast } from "@redbamboo/ui"
import { api } from "../lib/api"
import {
  overlayReactions, pendingKey, settlePending,
  type PendingReactions, type ReactionMap,
} from "../lib/reaction-overlay"

// A confirmation that never arrives (a dropped socket) must not pin a click forever.
const CONFIRMATION_TIMEOUT_MS = 10_000

export type { ReactionActor, ReactionGroup, ReactionMap } from "../lib/reaction-overlay"

interface ReactionEvent {
  type: "discussion.reaction"
  data: {
    discussionId: string
    messageKey: string
    emoji: string
    action: "add" | "remove"
    actorId: string
    actorName: string
    actorType: "user" | "agent"
  }
}

export function useReactions(discussionId: string | null) {
  const [reactions, setReactions] = useState<ReactionMap>({})
  const [pending, setPending] = useState<PendingReactions>({})
  const pendingRef = useRef(pending)
  pendingRef.current = pending
  const loadedRef = useRef<string | null>(null)
  const { toast } = useToast()

  const load = useCallback((id: string) => {
    api.get<{ reactions: ReactionMap }>(`/api/apps/nova/discussions/${id}/reactions`)
      .then((data) => { if (loadedRef.current === id) setReactions(data.reactions ?? {}) })
      .catch(() => { if (loadedRef.current === id) setReactions({}) })
  }, [])

  useEffect(() => {
    if (!discussionId) { setReactions({}); setPending({}); loadedRef.current = null; return }
    if (loadedRef.current === discussionId) return
    loadedRef.current = discussionId
    setPending({})
    load(discussionId)
  }, [discussionId, load])

  // A click stays shown until the server's own event confirms it.
  useEffect(() => { setPending((current) => settlePending(reactions, current)) }, [reactions])

  useWsSubscribe((event: unknown) => {
    const e = event as ReactionEvent
    if (e.type !== "discussion.reaction") return
    if (e.data.discussionId !== discussionId) return

    const { messageKey, emoji, action, actorId, actorName, actorType } = e.data

    setReactions((prev) => {
      const msgReactions = [...(prev[messageKey] ?? [])]
      const idx = msgReactions.findIndex((r) => r.emoji === emoji)

      if (action === "add") {
        const existing = idx >= 0 ? msgReactions[idx] : undefined
        if (existing) {
          if (existing.actors.some((a) => a.id === actorId)) return prev
          msgReactions[idx] = {
            ...existing,
            count: existing.count + 1,
            actors: [...existing.actors, { id: actorId, name: actorName, type: actorType as "user" | "agent" }],
            userReacted: existing.userReacted || actorType === "user",
          }
        } else {
          msgReactions.push({
            emoji,
            count: 1,
            actors: [{ id: actorId, name: actorName, type: actorType as "user" | "agent" }],
            userReacted: actorType === "user",
          })
        }
      } else {
        if (idx < 0) return prev
        const existing = msgReactions[idx]!
        const newActors = existing.actors.filter((a) => a.id !== actorId)
        if (newActors.length === 0) {
          msgReactions.splice(idx, 1)
        } else {
          msgReactions[idx] = {
            ...existing,
            count: newActors.length,
            actors: newActors,
            userReacted: newActors.some((a) => a.type === "user"),
          }
        }
      }

      return { ...prev, [messageKey]: msgReactions }
    })
  })

  const send = useCallback(async (messageKey: string, emoji: string, action: "add" | "remove") => {
    if (!discussionId) return
    const key = pendingKey(messageKey, emoji)
    const drop = () => setPending((current) => {
      if (!(key in current)) return current
      const { [key]: _, ...rest } = current
      return rest
    })
    setPending((current) => ({ ...current, [key]: action }))
    try {
      await api.post(`/api/apps/nova/discussions/${discussionId}/reactions${action === "remove" ? "/remove" : ""}`, { messageKey, emoji })
    } catch (err) {
      drop()
      toast({
        variant: "error",
        title: action === "add" ? "Reaction not saved" : "Reaction not removed",
        description: err instanceof Error ? err.message : "Unknown error",
      })
      return
    }
    setTimeout(() => {
      if (loadedRef.current !== discussionId || !(key in pendingRef.current)) return
      drop()
      load(discussionId)
    }, CONFIRMATION_TIMEOUT_MS)
  }, [discussionId, load, toast])

  const react = useCallback((messageKey: string, emoji: string) => send(messageKey, emoji, "add"), [send])
  const unreact = useCallback((messageKey: string, emoji: string) => send(messageKey, emoji, "remove"), [send])

  const shown = useMemo(() => overlayReactions(reactions, pending), [reactions, pending])
  return { reactions: shown, react, unreact }
}
