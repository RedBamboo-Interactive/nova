import { useState, useCallback, useEffect, useRef, useMemo, startTransition } from "react"
import { useToast } from "@redbamboo/ui"
import { api } from "@/lib/api"
import type { DiscussionInfo, DiscussionMessage, ClaudeStreamEvent, WsEvent, EventType } from "@/lib/types"
import type { MessageBlock, MessagePart, PendingQuestion, ChatEvent, ImageAttachment } from "@redbamboo/chat"
import { processStreamEvent, rebuildBlocks } from "@redbamboo/chat"
import type { PersistedMessage } from "@redbamboo/chat"

function stripContextXml(content: string): string {
  return content
    .replace(/<nova-context[\s\S]*?<\/nova-context>\s*/g, "")
    .replace(/<nova-prior-messages?[\s\S]*?<\/nova-prior-messages?>\s*/g, "")
    .replace(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/g, "$1")
    .trim()
}

function isEventMessage(m: MessageBlock): boolean {
  const source = m.metadata?.source as string | undefined
  if (source?.startsWith("event:")) return true
  const text = m.parts[0]?.content ?? ""
  return /<nova-event\s/.test(text)
}

type EventResolver = (source: string) => EventType

function formatEventMessage(m: MessageBlock, resolve?: EventResolver): MessageBlock {
  const source = (m.metadata?.source as string | undefined) ?? "event:system"
  const key = source.replace(/^event:/, "").split(":")[0] ?? "system"
  const text = m.parts[0]?.content ?? ""
  const cleaned = text.replace(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/g, "$1").trim() || text
  const eventType = resolve?.(source)
  return {
    ...m,
    role: "assistant",
    parts: [{ type: "tool_use", toolName: `event:${key}`, toolInput: JSON.stringify({ event: cleaned, icon: eventType?.icon ?? null, color: eventType?.color ?? null }), content: cleaned }],
  }
}

function cleanMessages(blocks: MessageBlock[], resolve?: EventResolver): MessageBlock[] {
  const result: MessageBlock[] = []
  let eventGroup: MessageBlock[] = []

  const flushEvents = () => {
    if (eventGroup.length === 0) return
    const parts = eventGroup.map((m) => {
      const formatted = formatEventMessage(m, resolve)
      return formatted.parts[0]!
    })
    result.push({
      ...eventGroup[0]!,
      role: "assistant",
      parts,
    })
    eventGroup = []
  }

  for (const m of blocks) {
    if (isEventMessage(m)) {
      eventGroup.push(m)
      continue
    }
    flushEvents()
    if (m.role !== "user") { result.push(m); continue }
    const textPart = m.parts.find((p) => p.type === "text")
    if (!textPart?.content || !textPart.content.includes("<nova-")) { result.push(m); continue }
    const cleaned = stripContextXml(textPart.content)
    if (cleaned === textPart.content) { result.push(m); continue }
    if (!cleaned) continue
    result.push({
      ...m,
      parts: m.parts.map((p) => p === textPart ? { ...p, content: cleaned } : p),
    })
  }
  flushEvents()
  return result
}

function toChatMessages(messages: DiscussionMessage[]): MessageBlock[] {
  return messages.map((m) => ({
    id: m.id,
    role: m.role,
    parts: m.parts.map((p): MessagePart => ({
      type: p.type === "tool_use" || p.type === "tool_result" ? p.type : p.type === "audio" ? "audio" : "text",
      content: p.content,
      toolName: p.toolName,
      toolInput: p.toolInput,
    })),
    timestamp: m.timestamp,
    senderAgentId: m.senderAgentId,
    metadata: m.source ? { source: m.source } : undefined,
  }))
}

export function useDiscussions() {
  const { toast } = useToast()
  const [discussions, setDiscussions] = useState<DiscussionInfo[]>([])
  const [activeDiscussionId, setActiveDiscussionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, MessageBlock[]>>({})
  const [streaming, setStreaming] = useState<Record<string, boolean>>({})
  const [pendingQuestions, setPendingQuestions] = useState<Record<string, PendingQuestion | null>>({})
  const [dismissedIds, setDismissedIds] = useState<Set<string>>(new Set())
  const [isSpawning, setIsSpawning] = useState(false)
  const [upstreamConnected, setUpstreamConnected] = useState(true)
  const [loadingDiscussionId, setLoadingDiscussionId] = useState<string | null>(null)
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

    const disc = discussions.find((d) => d.id === id)
    if (!disc) return

    setLoadingDiscussionId(id)
    loadedRef.current.add(id)
    try {
      if (disc?.type === "live" && disc?.sessionId) {
        // LIVE: merge session messages (chat) with Nova API messages (events)
        let sessionMsgs: MessageBlock[] = []
        try {
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
          if (data.messages?.length) sessionMsgs = rebuildBlocks(data.messages)
        } catch {}

        let apiMsgs: MessageBlock[] = []
        try {
          const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/discussions/${id}`)
          if (data.messages?.length) apiMsgs = toChatMessages(data.messages)
        } catch {}

        const seen = new Set<string>()
        const merged = [...sessionMsgs, ...apiMsgs]
          .filter((m) => {
            const key = `${m.timestamp}:${m.parts[0]?.content?.slice(0, 50)}`
            if (seen.has(key)) return false
            seen.add(key)
            return true
          })
          .sort((a, b) => new Date(a.timestamp ?? 0).getTime() - new Date(b.timestamp ?? 0).getTime())

        setMessages((prev) => ({ ...prev, [id]: cleanMessages(merged) }))
        return
      }

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
            setMessages((prev) => ({ ...prev, [id]: cleanMessages(rebuildBlocks(data.messages)) }))
            return
          }
        } catch {
          try {
            await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
            const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
            if (data.messages?.length) {
              setMessages((prev) => ({ ...prev, [id]: cleanMessages(rebuildBlocks(data.messages)) }))
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
          setMessages((prev) => ({ ...prev, [id]: cleanMessages(toChatMessages(data.messages)) }))
        }
      } catch { /* discussion not found */ }
    } finally {
      setLoadingDiscussionId((cur) => cur === id ? null : cur)
    }
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
    startTransition(() => {
      loadMessages(id)
      api.put(`/api/discussions/${id}/read`).catch(() => {})
      setDiscussions((prev) =>
        prev.map((d) => d.id === id ? { ...d, lastReadAt: new Date().toISOString() } : d)
      )
    })
  }, [loadMessages])

  const visibleDiscussions = useMemo(
    () => discussions.filter((d) => d.status !== "archived"),
    [discussions],
  )

  useEffect(() => {
    if (activeDiscussionId && !loadedRef.current.has(activeDiscussionId)) {
      loadMessages(activeDiscussionId)
    }
  }, [discussions, activeDiscussionId, loadMessages])

  const autoSelected = useRef(false)
  useEffect(() => {
    if (autoSelected.current || activeDiscussionId) return
    const live = visibleDiscussions.find((d) => d.type === "live")
    const target = live ?? visibleDiscussions[0]
    if (target) {
      autoSelected.current = true
      selectDiscussion(target.id)
    }
  }, [visibleDiscussions, activeDiscussionId, selectDiscussion])

  const createDiscussion = useCallback(async (agentId?: string) => {
    setIsSpawning(true)
    try {
      const body = agentId ? { agentId } : undefined
      const d = await api.post<DiscussionInfo>("/api/discussions", body)
      setDiscussions((prev) => prev.some((x) => x.id === d.id) ? prev : [d, ...prev])
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
    if (!disc) return

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
      const displayContent = content
        .replace(/<nova-context[\s\S]*?<\/nova-context>\s*/g, "")
        .replace(/<nova-prior-messages?[\s\S]*?<\/nova-prior-messages?>\s*/g, "")
        .trim()
      const title = displayContent.length > 60 ? displayContent.slice(0, 59) + "…" : displayContent
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

    const updateSessionId = (res: { sessionId?: string }) => {
      if (res.sessionId && res.sessionId !== disc.sessionId) {
        setDiscussions((prev) =>
          prev.map((d) => d.id === discussionId ? { ...d, sessionId: res.sessionId! } : d)
        )
      }
    }

    const backfillMetadata = (meta?: Record<string, unknown>) => {
      if (!meta) return
      setMessages((prev) => ({
        ...prev,
        [discussionId]: (prev[discussionId] ?? []).map((m) =>
          m.id === userMsg.id ? { ...m, metadata: meta } : m
        ),
      }))
    }

    try {
      const res = await api.post<{ success: boolean; sessionId?: string; metadata?: Record<string, unknown> }>(`/api/discussions/${discussionId}/message`, { content, images, inputMethod })
      updateSessionId(res)
      backfillMetadata(res.metadata)
      return
    } catch {
      if (!disc.sessionId) { fail(); return }
    }

    try {
      await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
    } catch {
      fail()
      return
    }

    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const res = await api.post<{ success: boolean; sessionId?: string; metadata?: Record<string, unknown> }>(`/api/discussions/${discussionId}/message`, { content, images, inputMethod })
        updateSessionId(res)
        backfillMetadata(res.metadata)
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
        let wasArchived = false
        setDiscussions((prev) =>
          prev.map((d) => {
            if (d.id !== discId || d.status === "archived") { if (d.id === discId) wasArchived = true; return d }
            if (d.status === "thinking") return d
            return {
              ...d,
              status: discStatus,
              lastActivity: now,
              ...(isViewing && !isStopped ? { lastReadAt: now } : {}),
            }
          })
        )
        if (wasArchived) return
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
      let wasArchived = false
      setDiscussions((prev) =>
        prev.map((d) => {
          if (d.id === discId && d.status === "archived") { wasArchived = true; return d }
          return d.id === discId ? { ...d, status: "stopped" as const } : d
        })
      )
      if (!wasArchived) api.put(`/api/discussions/${discId}/stopped`).catch(() => {})
    } else if (event.type === "discussion.created") {
      const { discussionId, agentId, status, type } = event.data as { discussionId: string; agentId?: string; status?: string; type?: string }
      if (!discussionId) return
      setDiscussions((prev) => {
        if (prev.some((d) => d.id === discussionId)) return prev
        const newDisc: DiscussionInfo = {
          id: discussionId,
          title: null,
          sessionId: null,
          status: (status ?? "idle") as DiscussionInfo["status"],
          type: (type ?? "chat") as DiscussionInfo["type"],
          createdAt: new Date().toISOString(),
          lastActivity: new Date().toISOString(),
          messageCount: 0,
          lastReadAt: null,
          agentId: agentId ?? null,
        }
        return [newDisc, ...prev]
      })
    } else if (event.type === "discussion.event") {
      const { discussionId, content, source, senderAgentId } = event.data as { discussionId: string; sessionId: string; content: string; source: string; senderAgentId?: string }
      if (!discussionId) return
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const sourceKey = source ? `event:${source}` : "event:system"
        const key = source?.split(":")[0] ?? "system"
        const cleaned = content.replace(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/g, "$1").trim() || content
        const newBlock: import("@redbamboo/chat").MessageBlock = {
          id: `event-${Date.now()}`,
          role: "assistant",
          parts: [{ type: "tool_use", toolName: key, toolInput: JSON.stringify({ event: cleaned }), content: cleaned }],
          timestamp: new Date().toISOString(),
          senderAgentId,
          metadata: { source: sourceKey },
        }
        return { ...prev, [discussionId]: [...current, newBlock] }
      })
    } else if (event.type === "discussion.nova-message") {
      const { discussionId, content, audioUrl, senderAgentId } = event.data as { discussionId: string; content: string; audioUrl?: string; senderAgentId?: string }
      if (!discussionId) return
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const parts: import("@redbamboo/chat").MessagePart[] = [{ type: "text", content }]
        if (audioUrl) parts.push({ type: "audio", content: audioUrl })
        const newBlock: import("@redbamboo/chat").MessageBlock = {
          id: `nova-msg-${Date.now()}`,
          role: "assistant",
          parts,
          timestamp: new Date().toISOString(),
          senderAgentId,
        }
        return { ...prev, [discussionId]: [...current, newBlock] }
      })
    } else if (event.type === "discussion.cleared") {
      const { discussionId } = event.data as { discussionId: string }
      if (!discussionId) return
      setMessages((prev) => ({ ...prev, [discussionId]: [] }))
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
      setPendingQuestions((prev) => ({ ...prev, [discussionId]: null }))
      loadedRef.current.delete(discussionId)
      loadMessages(discussionId)
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

  const handleUpstreamDisconnect = useCallback(() => {
    setUpstreamConnected(false)
    setStreaming({})
    setPendingQuestions({})
  }, [])

  const handleUpstreamReconnect = useCallback(() => {
    setUpstreamConnected(true)
    refreshDiscussions()
    reloadActiveMessages(true)
  }, [refreshDiscussions, reloadActiveMessages])

  const archiveDiscussion = useCallback(async (id: string) => {
    const disc = discussions.find((d) => d.id === id)
    if (disc?.type === "live") {
      toast({ variant: "error", title: "Can't archive", description: "Live discussions cannot be archived" })
      return
    }
    setDiscussions((ds) => ds.map((d) => d.id === id ? { ...d, status: "archived" as const } : d))
    if (activeDiscussionId === id) setActiveDiscussionId(null)
    try {
      await api.delete(`/api/discussions/${id}`)
    } catch (err) {
      if (disc) setDiscussions((ds) => ds.map((d) => d.id === id ? { ...d, status: disc.status } : d))
      toast({ variant: "error", title: "Failed to archive", description: err instanceof Error ? err.message : "Unknown error" })
    }
  }, [activeDiscussionId, discussions, toast])

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
    isLoadingMessages: loadingDiscussionId === activeDiscussionId && loadingDiscussionId !== null,
    upstreamConnected,
    handleUpstreamDisconnect,
    handleUpstreamReconnect,
  }
}
