import { useState, useEffect, useCallback, useRef } from "react"
import { useWsSubscribe } from "@redbamboo/utility"
import { api } from "../lib/api"

export interface ReactionActor {
  id: string
  name: string
  type: "user" | "agent"
}

export interface ReactionGroup {
  emoji: string
  count: number
  actors: ReactionActor[]
  userReacted: boolean
}

export type ReactionMap = Record<string, ReactionGroup[]>

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
  const loadedRef = useRef<string | null>(null)

  useEffect(() => {
    if (!discussionId) { setReactions({}); loadedRef.current = null; return }
    if (loadedRef.current === discussionId) return
    loadedRef.current = discussionId
    api.get<{ reactions: ReactionMap }>(`/api/apps/nova/discussions/${discussionId}/reactions`)
      .then((data) => setReactions(data.reactions ?? {}))
      .catch(() => setReactions({}))
  }, [discussionId])

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

  const react = useCallback(async (messageKey: string, emoji: string) => {
    if (!discussionId) return
    await api.post(`/api/apps/nova/discussions/${discussionId}/reactions`, { messageKey, emoji })
  }, [discussionId])

  const unreact = useCallback(async (messageKey: string, emoji: string) => {
    if (!discussionId) return
    await api.post(`/api/apps/nova/discussions/${discussionId}/reactions/remove`, { messageKey, emoji })
  }, [discussionId])

  return { reactions, react, unreact }
}
