import { useState, useCallback, useEffect, useRef, useMemo } from "react"
import { api } from "@/lib/api"
import type { DiscussionInfo, DiscussionMessage, ClaudeStreamEvent, WsEvent } from "@/lib/types"
import type { MessageBlock, MessagePart, PendingQuestion, ChatEvent, ImageAttachment } from "@redbamboo/chat"
import { processStreamEvent, rebuildBlocks } from "@redbamboo/chat"
import type { PersistedMessage } from "@redbamboo/chat"

function toChatMessages(messages: DiscussionMessage[]): MessageBlock[] {
  return messages.map((m) => ({
    id: m.id,
    role: m.role,
    parts: m.parts.map((p): MessagePart => ({
      type: p.type === "tool_use" || p.type === "tool_result" ? p.type : "text",
      content: p.content,
      toolName: p.toolName,
      toolInput: p.toolInput,
    })),
    timestamp: m.timestamp,
  }))
}

export function useDiscussions() {
  const [discussions, setDiscussions] = useState<DiscussionInfo[]>([])
  const [activeDiscussionId, setActiveDiscussionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, MessageBlock[]>>({})
  const [streaming, setStreaming] = useState<Record<string, boolean>>({})
  const [pendingQuestions, setPendingQuestions] = useState<Record<string, PendingQuestion | null>>({})
  const [dismissedIds, setDismissedIds] = useState<Set<string>>(new Set())
  const [isSpawning, setIsSpawning] = useState(false)
  const loadedRef = useRef<Set<string>>(new Set())

  const activeDiscussion = discussions.find((d) => d.id === activeDiscussionId) ?? null
  const activeMessages = activeDiscussionId ? messages[activeDiscussionId] ?? [] : []
  const isStreaming = activeDiscussionId ? streaming[activeDiscussionId] ?? false : false
  const activePendingQuestion = activeDiscussionId ? pendingQuestions[activeDiscussionId] ?? null : null

  const activeIdRef = useRef(activeDiscussionId)
  activeIdRef.current = activeDiscussionId
  const streamingRef = useRef(streaming)
  streamingRef.current = streaming

  const sessionToDiscussion = useMemo(() => {
    const map = new Map<string, string>()
    for (const d of discussions) {
      if (d.sessionId) map.set(d.sessionId, d.id)
    }
    return map
  }, [discussions])

  const refreshDiscussions = useCallback(async () => {
    const list = await api.get<DiscussionInfo[]>("/api/discussions")
    setDiscussions(list.filter((d) => !dismissedIds.has(d.id)))
  }, [dismissedIds])

  const syncAndRefresh = useCallback(async () => {
    await api.post("/api/discussions/sync").catch(() => {})
    await refreshDiscussions()
  }, [refreshDiscussions])

  useEffect(() => {
    syncAndRefresh()
  }, [syncAndRefresh])

  const loadMessages = useCallback(async (id: string) => {
    if (loadedRef.current.has(id)) return
    loadedRef.current.add(id)

    const disc = discussions.find((d) => d.id === id)
    if (disc?.sessionId) {
      try {
        const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
        if (data.session?.title && data.session.title !== disc.title) {
          setDiscussions((prev) =>
            prev.map((d) => d.id === id ? { ...d, title: data.session.title! } : d)
          )
          api.put(`/api/discussions/${id}/title`, { title: data.session.title }).catch(() => {})
        }
        if (data.messages?.length) {
          setMessages((prev) => ({ ...prev, [id]: rebuildBlocks(data.messages) }))
          return
        }
      } catch {
        // Session may be dead — try to resume so it's ready for messages
        try {
          await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
          if (data.messages?.length) {
            setMessages((prev) => ({ ...prev, [id]: rebuildBlocks(data.messages) }))
            return
          }
        } catch {
          // Resume failed — fall through to legacy load
        }
      }
    }

    try {
      const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/discussions/${id}`)
      if (data.messages?.length) {
        setMessages((prev) => ({ ...prev, [id]: toChatMessages(data.messages) }))
      }
    } catch { /* discussion not found */ }
  }, [discussions])

  const reloadActiveMessages = useCallback((force?: boolean) => {
    const id = activeIdRef.current
    if (!id) return
    if (force) {
      setStreaming((prev) => ({ ...prev, [id]: false }))
    } else if (streamingRef.current[id]) {
      return
    }
    loadedRef.current.delete(id)
    loadMessages(id)
  }, [loadMessages])

  const selectDiscussion = useCallback((id: string) => {
    setActiveDiscussionId(id)
    loadMessages(id)
    api.put(`/api/discussions/${id}/read`).catch(() => {})
    setDiscussions((prev) =>
      prev.map((d) => d.id === id ? { ...d, lastReadAt: new Date().toISOString() } : d)
    )
  }, [loadMessages])

  const visibleDiscussions = useMemo(
    () => discussions.filter((d) => d.status !== "archived"),
    [discussions],
  )

  const autoSelected = useRef(false)
  useEffect(() => {
    const first = visibleDiscussions[0]
    if (!autoSelected.current && !activeDiscussionId && first) {
      autoSelected.current = true
      selectDiscussion(first.id)
    }
  }, [visibleDiscussions, activeDiscussionId, selectDiscussion])

  const createDiscussion = useCallback(async () => {
    setIsSpawning(true)
    setActiveDiscussionId(null)
    try {
      const d = await api.post<DiscussionInfo>("/api/discussions")
      setDiscussions((prev) => [d, ...prev])
      setActiveDiscussionId(d.id)
      setMessages((prev) => ({ ...prev, [d.id]: [] }))
      loadedRef.current.add(d.id)
      return d
    } finally {
      setIsSpawning(false)
    }
  }, [])

  const sendMessage = useCallback(async (discussionId: string, content: string, images?: ImageAttachment[], inputMethod?: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return

    const userMsg: MessageBlock = {
      id: crypto.randomUUID(),
      role: "user",
      parts: [{ type: "text", content, images }],
      timestamp: new Date().toISOString(),
    }
    setMessages((prev) => ({
      ...prev,
      [discussionId]: [...(prev[discussionId] ?? []), userMsg],
    }))

    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
    setDiscussions((prev) =>
      prev.map((d) => d.id === discussionId ? { ...d, status: "thinking" as const } : d)
    )

    if (!disc.title && disc.messageCount === 0) {
      const title = content.length > 60 ? content.slice(0, 59) + "…" : content
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, title } : d)
      )
      api.put(`/api/discussions/${discussionId}/title`, { title }).catch(() => {})
    }

    const fail = () => {
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: "stopped" as const } : d)
      )
    }

    try {
      await api.post(`/api/discussions/${discussionId}/message`, { content, images, inputMethod })
      return
    } catch {
      // Session may be dead after RedCompute restart — try to resume
    }

    try {
      await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
    } catch {
      fail()
      return
    }

    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        await api.post(`/api/discussions/${discussionId}/message`, { content, images, inputMethod })
        return
      } catch {
        if (attempt < 2) {
          await new Promise((r) => setTimeout(r, 1000))
          continue
        }
        fail()
      }
    }
  }, [discussions])

  const interruptDiscussion = useCallback(async (discussionId: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return
    try {
      await api.post(`/ai-session/sessions/${disc.sessionId}/interrupt`)
    } catch { /* best effort */ }
  }, [discussions])

  const answerQuestion = useCallback(async (discussionId: string, answer: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return
    setPendingQuestions((prev) => ({ ...prev, [discussionId]: null }))
    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
    try {
      await api.post(`/ai-session/sessions/${disc.sessionId}/answer`, { answer })
    } catch {
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
    }
  }, [discussions])

  const resumeDiscussion = useCallback(async (discussionId: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return
    try {
      await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: "idle" as const } : d)
      )
    } catch { /* resume failed — stays stopped */ }
  }, [discussions])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "session.updated") {
      const session = event.data as { id: string; status: string; title?: string }
      const discId = sessionToDiscussion.get(session.id)
      if (!discId) return
      if (session.status !== "Active") {
        setStreaming((prev) => ({ ...prev, [discId]: false }))
        const isStopped = session.status === "Stopped" || session.status === "Error"
        const discStatus = isStopped ? "stopped" as const : "idle" as const
        const now = new Date().toISOString()
        const isViewing = activeIdRef.current === discId
        setDiscussions((prev) =>
          prev.map((d) => {
            if (d.id !== discId || d.status === "archived") return d
            if (d.status === "thinking") return d
            return {
              ...d,
              status: discStatus,
              lastActivity: now,
              ...(isViewing && !isStopped ? { lastReadAt: now } : {}),
            }
          })
        )
        if (isStopped) {
          api.put(`/api/discussions/${discId}/stopped`).catch(() => {})
        } else {
          api.put(`/api/discussions/${discId}/activity`).catch(() => {})
          if (isViewing) {
            api.put(`/api/discussions/${discId}/read`).catch(() => {})
          }
          const syncTitle = (name: string) => {
            setDiscussions((prev) =>
              prev.map((d) => d.id === discId ? { ...d, title: name } : d)
            )
            api.put(`/api/discussions/${discId}/title`, { title: name }).catch(() => {})
          }
          if (session.title) {
            syncTitle(session.title)
          } else {
            api.get<{ session: { title?: string } }>(`/ai-session/sessions/${session.id}`)
              .then((data) => { if (data.session?.title) syncTitle(data.session.title) })
              .catch(() => {})
          }
        }
      }
    } else if (event.type === "session.ended") {
      const { id } = event.data as { id: string }
      const discId = sessionToDiscussion.get(id)
      if (!discId) return
      setStreaming((prev) => ({ ...prev, [discId]: false }))
      setPendingQuestions((prev) => ({ ...prev, [discId]: null }))
      setDiscussions((prev) =>
        prev.map((d) => d.id === discId && d.status !== "archived" ? { ...d, status: "stopped" as const } : d)
      )
      api.put(`/api/discussions/${discId}/stopped`).catch(() => {})
    } else if (event.type === "discussion.event") {
      const { discussionId, content } = event.data as { discussionId: string; sessionId: string; content: string; source: string }
      if (!discussionId) return
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const newBlock: import("@redbamboo/chat").MessageBlock = {
          id: `event-${Date.now()}`,
          role: "user",
          parts: [{ type: "text", content }],
          timestamp: new Date().toISOString(),
        }
        return { ...prev, [discussionId]: [...current, newBlock] }
      })
    } else if (event.type === "session.stream") {
      const { sessionId, event: evt } = event.data as { sessionId: string; event: ClaudeStreamEvent }
      const discId = sessionToDiscussion.get(sessionId)
      if (!discId) return

      const chatEvent: ChatEvent = {
        type: evt.type as ChatEvent["type"],
        content: evt.content ?? null,
        toolName: evt.toolName ?? null,
        toolInput: typeof evt.toolInput === "string"
          ? evt.toolInput
          : evt.toolInput ? JSON.stringify(evt.toolInput) : null,
        toolResult: evt.toolResult ?? null,
        messageId: evt.messageId ?? null,
      }

      setMessages((prev) => {
        const current = prev[discId] ?? []
        const result = processStreamEvent(current, true, chatEvent)
        setStreaming((p) => ({ ...p, [discId]: result.isStreaming }))
        if (result.pendingQuestion) {
          setPendingQuestions((p) => ({ ...p, [discId]: result.pendingQuestion }))
        } else if (chatEvent.type === "status") {
          setPendingQuestions((p) => ({ ...p, [discId]: null }))
          if (!result.isStreaming) {
            setDiscussions((p) =>
              p.map((d) => d.id === discId ? { ...d, status: "idle" as const } : d)
            )
          }
        }
        return { ...prev, [discId]: result.messages }
      })
    }
  }, [sessionToDiscussion])

  const archiveDiscussion = useCallback((id: string) => {
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, status: "archived" as const } : d))
    if (activeDiscussionId === id) setActiveDiscussionId(null)
    api.delete(`/api/discussions/${id}`).catch(() => {})
  }, [activeDiscussionId])

  const dismissDiscussion = useCallback((id: string) => {
    setDismissedIds((prev) => new Set(prev).add(id))
    setDiscussions((prev) => prev.filter((d) => d.id !== id))
    if (activeDiscussionId === id) setActiveDiscussionId(null)
  }, [activeDiscussionId])

  const renameDiscussion = useCallback(async (id: string, title: string) => {
    await api.put(`/api/discussions/${id}/title`, { title })
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, title } : d))
  }, [])

  return {
    discussions: visibleDiscussions,
    activeDiscussion,
    activeDiscussionId,
    activeMessages,
    isStreaming,
    isSpawning,
    pendingQuestion: activePendingQuestion,
    selectDiscussion,
    createDiscussion,
    sendMessage,
    interruptDiscussion,
    answerQuestion,
    archiveDiscussion,
    dismissDiscussion,
    renameDiscussion,
    resumeDiscussion,
    refreshDiscussions,
    syncAndRefresh,
    reloadActiveMessages,
    handleWsEvent,
  }
}
