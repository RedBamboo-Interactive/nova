import { useState, useCallback, useEffect, useRef, useMemo, startTransition } from "react"
import { useToast } from "@redbamboo/ui"
import { api } from "../lib/api"
import type { DiscussionInfo, DiscussionMessage, ClaudeStreamEvent, WsEvent, EventType } from "../lib/types"
import type { MessageBlock, MessagePart, PendingQuestion, ChatEvent, ImageAttachment } from "@redbamboo/chat"
import { processStreamEvent, rebuildBlocks } from "@redbamboo/chat"
import type { PersistedMessage } from "@redbamboo/chat"

function isClosed(status: string | undefined): boolean {
  return status === "archived" || status === "archiving"
}

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
  const data = (m.metadata?.eventData as Record<string, unknown> | undefined) ?? null
  return {
    ...m,
    role: "assistant",
    parts: [{
      type: "tool_use",
      toolName: `event:${key}`,
      // timestamp rides per-part: event groups merge blocks, keeping only the
      // first message's block timestamp.
      toolInput: JSON.stringify({ event: cleaned, icon: eventType?.icon ?? null, color: eventType?.color ?? null, data, timestamp: m.timestamp }),
      content: cleaned,
    }],
  }
}

function cleanMessages(blocks: MessageBlock[], resolve?: EventResolver): MessageBlock[] {
  const result: MessageBlock[] = []
  let eventGroup: MessageBlock[] = []

  const makeEventBlock = () => {
    if (eventGroup.length === 0) return null
    const parts = eventGroup.map((m) => formatEventMessage(m, resolve).parts[0]!)
    const block: MessageBlock = { ...eventGroup[0]!, role: "assistant", parts }
    eventGroup = []
    return block
  }

  const flushEvents = () => {
    const block = makeEventBlock()
    if (block) result.push(block)
  }

  for (const m of blocks) {
    if (isEventMessage(m)) {
      eventGroup.push(m)
      continue
    }
    // Only flush events before user messages, not between consecutive assistant blocks
    if (m.role === "user") {
      flushEvents()
    } else if (eventGroup.length > 0 && m.role === "assistant") {
      // Events between assistant blocks: hold them, insert before this assistant run
      const lastResult = result[result.length - 1]
      if (!lastResult || lastResult.role !== "assistant") {
        flushEvents()
      }
      // else: keep accumulating, will flush before next user message or at end
    }
    if (m.role === "user") {
      const textPart = m.parts.find((p) => p.type === "text")
      if (!textPart?.content || !textPart.content.includes("<nova-")) { result.push(m); continue }
      const cleaned = stripContextXml(textPart.content)
      if (cleaned === textPart.content) { result.push(m); continue }
      if (!cleaned) continue
      result.push({
        ...m,
        parts: m.parts.map((p) => p === textPart ? { ...p, content: cleaned } : p),
      })
    } else {
      result.push(m)
    }
  }
  flushEvents()

  // Post-process: move event blocks that ended up between assistant blocks to before the assistant run
  const final: MessageBlock[] = []
  for (let i = 0; i < result.length; i++) {
    const block = result[i]!
    const isEvent = block.parts.every((p) => p.toolName?.startsWith("event:"))
    const hasMoreAssistantContent = result.slice(i + 1).some(b =>
      b.role === "assistant" && !b.parts.every(p => p.toolName?.startsWith("event:"))
    )
    if (isEvent && final.length > 0 && final[final.length - 1]!.role === "assistant" && hasMoreAssistantContent) {
      // Find where this assistant run started
      let insertAt = final.length - 1
      while (insertAt > 0 && final[insertAt - 1]!.role === "assistant" && !final[insertAt - 1]!.parts.every((p) => p.toolName?.startsWith("event:"))) {
        insertAt--
      }
      // Merge with existing event block at that position if present
      if (insertAt > 0 && final[insertAt - 1]!.parts.every((p) => p.toolName?.startsWith("event:"))) {
        final[insertAt - 1] = { ...final[insertAt - 1]!, parts: [...final[insertAt - 1]!.parts, ...block.parts] }
      } else {
        final.splice(insertAt, 0, block)
      }
    } else {
      final.push(block)
    }
  }
  return final
}

function toChatMessages(messages: DiscussionMessage[]): MessageBlock[] {
  return messages.map((m) => {
    // Structured event metadata arrives as a sibling event_data part — stash it
    // on the block so formatEventMessage can fold it into the event part.
    let eventData: Record<string, unknown> | undefined
    const eventDataPart = m.parts.find((p) => p.type === "event_data")
    if (eventDataPart?.content) {
      try {
        const parsed = JSON.parse(eventDataPart.content)
        if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) eventData = parsed
      } catch { /* legacy or garbled — text-only event */ }
    }
    const metadata: Record<string, unknown> = {}
    if (m.source) metadata.source = m.source
    if (eventData) metadata.eventData = eventData
    return {
      id: m.messageUid ?? m.id,
      role: m.role,
      parts: m.parts
        .filter((p) => p.type !== "event_data")
        .map((p): MessagePart => ({
          type: p.type === "tool_use" || p.type === "tool_result" ? p.type : p.type === "audio" ? "audio" : "text",
          content: p.content,
          toolName: p.toolName,
          toolInput: p.toolInput,
        })),
      timestamp: m.timestamp,
      senderAgentId: m.senderAgentId,
      metadata: Object.keys(metadata).length > 0 ? metadata : undefined,
    }
  })
}

export function useDiscussions(eventResolver?: EventResolver) {
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
  // Synchronous view of the list for event handlers: reading status via a
  // setState updater's side effect is not reliable (updaters may run later).
  const discussionsRef = useRef(discussions)
  discussionsRef.current = discussions
  // Archive intents in flight (and confirmed): a stale list refresh racing the
  // DELETE must not resurrect these rows.
  const pendingArchivesRef = useRef<Set<string>>(new Set())
  const gpsRef = useRef<{ latitude: number; longitude: number; accuracy?: number } | null>(null)
  const lastPostedGpsRef = useRef<{ latitude: number; longitude: number } | null>(null)

  useEffect(() => {
    if (!navigator.geolocation) return
    const id = navigator.geolocation.watchPosition(
      (pos) => {
        gpsRef.current = {
          latitude: pos.coords.latitude,
          longitude: pos.coords.longitude,
          accuracy: pos.coords.accuracy ?? undefined,
        }
      },
      () => { gpsRef.current = null },
      { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 },
    )
    return () => navigator.geolocation.clearWatch(id)
  }, [])

  useEffect(() => {
    const interval = setInterval(() => {
      const gps = gpsRef.current
      if (!gps) return
      const last = lastPostedGpsRef.current
      if (last && last.latitude === gps.latitude && last.longitude === gps.longitude) return
      lastPostedGpsRef.current = { latitude: gps.latitude, longitude: gps.longitude }
      api.post("/api/apps/nova/location/update", {
        latitude: gps.latitude,
        longitude: gps.longitude,
        accuracy: gps.accuracy ?? null,
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      }).catch(() => {})
    }, 120_000)
    return () => clearInterval(interval)
  }, [])

  const sessionToDiscussion = useMemo(() => {
    const map = new Map<string, string>()
    for (const d of discussions) {
      if (d.sessionId) map.set(d.sessionId, d.id)
    }
    return map
  }, [discussions])

  const refreshDiscussions = useCallback(async () => {
    const list = await api.get<DiscussionInfo[]>("/api/apps/nova/discussions")
    setDiscussions(list
      .filter((d) => !dismissedIds.has(d.id))
      .map((d) => pendingArchivesRef.current.has(d.id) ? { ...d, status: "archiving" as const } : d))
  }, [dismissedIds])

  const syncAndRefresh = useCallback(async () => {
    await api.post("/api/apps/nova/discussions/sync").catch(() => {})
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
          const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}`)
          if (data.messages?.length) apiMsgs = toChatMessages(data.messages)
        } catch {}

        const seen = new Set<string>()
        // Nova API messages first: when both stores hold the same logical
        // message (shared uid), the Nova copy wins dedup — it carries the
        // source metadata that drives event rendering. Order on screen is
        // unaffected (the merge re-sorts by timestamp below).
        const merged = [...apiMsgs, ...sessionMsgs]
          .filter((m) => {
            // Blocks sharing an id are the same logical message cross-posted
            // to both stores (message uid); the timestamp+prefix key covers
            // records that predate the uid rollout.
            const key = `${m.timestamp}:${m.parts[0]?.content?.slice(0, 50)}`
            if (seen.has(m.id) || seen.has(key)) return false
            seen.add(m.id)
            seen.add(key)
            return true
          })
          .sort((a, b) => new Date(a.timestamp ?? 0).getTime() - new Date(b.timestamp ?? 0).getTime())

        setMessages((prev) => ({ ...prev, [id]: cleanMessages(merged, eventResolver) }))
        return
      }

      if (disc?.sessionId) {
        try {
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
          if (data.session?.title && data.session.title !== disc.title) {
            setDiscussions((prev) =>
              prev.map((d) => d.id === id ? { ...d, title: data.session.title! } : d)
            )
            api.put(`/api/apps/nova/discussions/${id}/title`, { title: data.session.title }).catch(() => {})
          }
          if (data.messages?.length) {
            setMessages((prev) => ({ ...prev, [id]: cleanMessages(rebuildBlocks(data.messages), eventResolver) }))
            return
          }
        } catch {
          try {
            await api.post(`/ai-session/sessions/${disc.sessionId}/resume`)
            const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
            if (data.messages?.length) {
              setMessages((prev) => ({ ...prev, [id]: cleanMessages(rebuildBlocks(data.messages), eventResolver) }))
              return
            }
          } catch {
            // Resume failed — fall through to legacy load
          }
        }
      }

      try {
        const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}`)
        if (data.messages?.length) {
          setMessages((prev) => ({ ...prev, [id]: cleanMessages(toChatMessages(data.messages), eventResolver) }))
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
      api.put(`/api/apps/nova/discussions/${id}/read`).catch(() => {})
      setDiscussions((prev) =>
        prev.map((d) => d.id === id ? { ...d, lastReadAt: new Date().toISOString() } : d)
      )
    })
  }, [loadMessages])

  const visibleDiscussions = useMemo(
    () => discussions.filter((d) => !isClosed(d.status)),
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
      const d = await api.post<DiscussionInfo>("/api/apps/nova/discussions", body)
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
      api.put(`/api/apps/nova/discussions/${discussionId}/title`, { title }).catch(() => {})
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

    // Re-key the optimistic block to the server-minted message uid so a
    // reaction added before reload survives it, and attach send metadata.
    const backfillMessage = (meta?: Record<string, unknown>, uid?: string | null) => {
      if (!meta && !uid) return
      setMessages((prev) => ({
        ...prev,
        [discussionId]: (prev[discussionId] ?? []).map((m) =>
          m.id === userMsg.id
            ? { ...m, ...(uid ? { id: uid } : {}), ...(meta ? { metadata: meta } : {}) }
            : m
        ),
      }))
    }

    const gps = gpsRef.current
    const gpsPayload = gps ? { latitude: gps.latitude, longitude: gps.longitude } : {}

    try {
      const res = await api.post<{ success: boolean; sessionId?: string; metadata?: Record<string, unknown>; messageUid?: string | null }>(`/api/apps/nova/discussions/${discussionId}/message`, { content, images, inputMethod, ...gpsPayload })
      updateSessionId(res)
      backfillMessage(res.metadata, res.messageUid)
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
        const res = await api.post<{ success: boolean; sessionId?: string; metadata?: Record<string, unknown>; messageUid?: string | null }>(`/api/apps/nova/discussions/${discussionId}/message`, { content, images, inputMethod, ...gpsPayload })
        updateSessionId(res)
        backfillMessage(res.metadata, res.messageUid)
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
        // Closed (archived/archiving) discussions are terminal: never echo
        // session events back to the server for them. Checked via the ref —
        // a setState updater's side effect is not guaranteed to have run here.
        const known = discussionsRef.current.find((d) => d.id === discId)
        if (!known || isClosed(known.status) || pendingArchivesRef.current.has(discId)) return
        const isStopped = session.status === "Stopped" || session.status === "Error"
        const discStatus = isStopped ? "stopped" as const : "idle" as const
        const now = new Date().toISOString()
        const isViewing = activeIdRef.current === discId
        setDiscussions((prev) =>
          prev.map((d) => {
            if (d.id !== discId || isClosed(d.status)) return d
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
          api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
        } else {
          api.put(`/api/apps/nova/discussions/${discId}/activity`).catch(() => {})
          if (isViewing) {
            api.put(`/api/apps/nova/discussions/${discId}/read`).catch(() => {})
          }
          const syncTitle = (name: string) => {
            setDiscussions((prev) =>
              prev.map((d) => d.id === discId ? { ...d, title: name } : d)
            )
            api.put(`/api/apps/nova/discussions/${discId}/title`, { title: name }).catch(() => {})
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
      const known = discussionsRef.current.find((d) => d.id === discId)
      const closed = !known || isClosed(known.status) || pendingArchivesRef.current.has(discId)
      setDiscussions((prev) =>
        prev.map((d) => d.id === discId && !isClosed(d.status) ? { ...d, status: "stopped" as const } : d)
      )
      if (!closed) api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
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
      const { discussionId, content, source, senderAgentId, metadata } = event.data as { discussionId: string; sessionId: string; content: string; source: string; senderAgentId?: string; metadata?: Record<string, unknown> | null }
      if (!discussionId) return
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const sourceKey = source ? `event:${source}` : "event:system"
        const key = source?.split(":")[0] ?? "system"
        const cleaned = content.replace(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/g, "$1").trim() || content
        const eventType = eventResolver?.(sourceKey)
        const eventPart: import("@redbamboo/chat").MessagePart = {
          type: "tool_use", toolName: `event:${key}`,
          toolInput: JSON.stringify({ event: cleaned, icon: eventType?.icon ?? null, color: eventType?.color ?? null, data: metadata ?? null, timestamp: new Date().toISOString() }),
          content: cleaned,
        }

        // Merge into the last block if it's already an event group
        const last = current[current.length - 1]
        if (last && last.metadata?.source && (last.metadata.source as string).startsWith("event:")) {
          const updated = [...current]
          updated[updated.length - 1] = { ...last, parts: [...last.parts, eventPart] }
          return { ...prev, [discussionId]: updated }
        }

        const newBlock: import("@redbamboo/chat").MessageBlock = {
          id: `event-${Date.now()}`,
          role: "assistant",
          parts: [eventPart],
          timestamp: new Date().toISOString(),
          senderAgentId,
          metadata: { source: sourceKey },
        }

        // If last block is assistant and still streaming, insert event before it
        if (last && last.role === "assistant" && !last.metadata?.source && last.parts.some(p => p.isPartial)) {
          const updated = [...current]
          updated.splice(updated.length - 1, 0, newBlock)
          return { ...prev, [discussionId]: updated }
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
    } else if (event.type === "discussion.rotated") {
      const { oldDiscussionId } = event.data as { oldDiscussionId: string; newDiscussionId: string; agentId: string }
      setDiscussions((prev) => prev.filter((d) => d.id !== oldDiscussionId))
      setMessages((prev) => { const next = { ...prev }; delete next[oldDiscussionId]; return next })
      loadedRef.current.delete(oldDiscussionId)
      if (activeDiscussionId === oldDiscussionId) setActiveDiscussionId(null)
      refreshDiscussions()
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
        messageUid: evt.messageUid ?? null,
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
    const disc = discussionsRef.current.find((d) => d.id === id)
    if (disc?.type === "live") {
      toast({ variant: "error", title: "Can't archive", description: "Live discussions cannot be archived" })
      return
    }
    // Optimistic close: record the intent first so list refreshes and session
    // events during the DELETE cannot resurrect the row, then hide it.
    pendingArchivesRef.current.add(id)
    setDiscussions((ds) => ds.map((d) => d.id === id ? { ...d, status: "archiving" as const } : d))
    if (activeIdRef.current === id) setActiveDiscussionId(null)
    try {
      await api.delete(`/api/apps/nova/discussions/${id}`)
      // Intent stays in the set after success: the server now owns the state
      // and default list fetches exclude closed discussions anyway.
    } catch (err) {
      pendingArchivesRef.current.delete(id)
      if (disc) setDiscussions((ds) => ds.map((d) => d.id === id ? { ...d, status: disc.status } : d))
      toast({ variant: "error", title: "Failed to archive", description: err instanceof Error ? err.message : "Unknown error" })
    }
  }, [toast])

  const dismissDiscussion = useCallback((id: string) => {
    setDismissedIds((prev) => new Set(prev).add(id))
    setDiscussions((prev) => prev.filter((d) => d.id !== id))
    if (activeDiscussionId === id) setActiveDiscussionId(null)
  }, [activeDiscussionId])

  const rotateDiscussion = useCallback(async (id: string) => {
    try {
      await api.post<{ archived: DiscussionInfo; created: DiscussionInfo }>(`/api/apps/nova/discussions/${id}/rotate`)
      setDiscussions((prev) => prev.filter((d) => d.id !== id))
      setMessages((prev) => { const next = { ...prev }; delete next[id]; return next })
      loadedRef.current.delete(id)
      toast({ variant: "success", title: "Timeline rotated", description: "Fresh LIVE discussion created" })
    } catch (err) {
      toast({ variant: "error", title: "Failed to rotate", description: err instanceof Error ? err.message : "Unknown error" })
    }
  }, [toast])

  const renameDiscussion = useCallback(async (id: string, title: string) => {
    await api.put(`/api/apps/nova/discussions/${id}/title`, { title })
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
    rotateDiscussion,
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
