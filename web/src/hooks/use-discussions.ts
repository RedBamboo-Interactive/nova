import { useState, useCallback, useEffect, useRef, useMemo, startTransition } from "react"
import { useToast } from "@redbamboo/ui"
import { api, ApiError } from "../lib/api"
import type { DiscussionInfo, DiscussionMessage, ClaudeStreamEvent, WsEvent, EventType } from "../lib/types"
import type { MessageBlock, MessagePart, PendingQuestion, QuestionAnswerPayload, QuestionOutcome, QuestionState, ChatEvent, ImageAttachment } from "@redbamboo/chat"
import { processStreamEvent, rebuildBlocks } from "@redbamboo/chat"
import type { PersistedMessage } from "@redbamboo/chat"
import { appendEvent, byTimestamp, isRawEventMessage, orderMessages } from "../lib/message-order"

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

type EventResolver = (source: string) => EventType

/**
 * Nova wraps every outgoing user message in context XML; the transcript shows
 * only the part the human typed. Returns null for a message that was pure
 * context and has nothing left to display.
 */
function stripContextBlocks(m: MessageBlock): MessageBlock | null {
  const textPart = m.parts.find((p) => p.type === "text")
  if (!textPart?.content || !textPart.content.includes("<nova-")) return m
  const cleaned = stripContextXml(textPart.content)
  if (cleaned === textPart.content) return m
  if (!cleaned) return null
  return { ...m, parts: m.parts.map((p) => p === textPart ? { ...p, content: cleaned } : p) }
}

/** Strip Nova's context wrappers, then apply the frieze ordering rule. */
function cleanMessages(blocks: MessageBlock[], resolve?: EventResolver): MessageBlock[] {
  const prepared: MessageBlock[] = []
  for (const block of blocks) {
    // Event detection has to run first: stripContextXml() unwraps <nova-event>
    // tags, which is one of the two markers identifying a legacy event message.
    if (isRawEventMessage(block)) {
      prepared.push(block)
      continue
    }
    if (block.role !== "user") {
      prepared.push(block)
      continue
    }
    const cleaned = stripContextBlocks(block)
    if (cleaned) prepared.push(cleaned)
  }
  return orderMessages(prepared, resolve)
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
          type: p.type === "tool_use" || p.type === "tool_result" ? p.type : p.type === "audio" ? "audio" : p.type === "image" ? "image" : "text",
          content: p.content ?? "",
          toolName: p.toolName,
          toolInput: p.toolInput,
          url: p.url,
          base64: p.base64,
          mediaType: p.mediaType,
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
  // How each discussion's last question ended, so a resolved card can say
  // "timed out" rather than the flat "Answered" it used to claim regardless.
  const [questionOutcomes, setQuestionOutcomes] = useState<Record<string, QuestionOutcome | null>>({})
  // processStreamEvent is pure — the question lifecycle has to be threaded back
  // in on every event, and from a ref so it is current inside the updater.
  const questionStatesRef = useRef<Record<string, QuestionState>>({})
  const [interrupting, setInterrupting] = useState<Record<string, boolean>>({})
  // True once the backend reports a discussion's CLI process was force-killed
  // and is being replaced, until the follow-up status/error lands. isStreaming
  // is already false by then, but a queued message isn't safe to send yet —
  // see @redbamboo/chat's process-stream-event.ts "killed" handling.
  const [resumePending, setResumePending] = useState<Record<string, boolean>>({})
  const resumePendingRef = useRef(resumePending)
  resumePendingRef.current = resumePending
  const [dismissedIds, setDismissedIds] = useState<Set<string>>(new Set())
  const [isSpawning, setIsSpawning] = useState(false)
  const [upstreamConnected, setUpstreamConnected] = useState(true)
  const [loadingDiscussionId, setLoadingDiscussionId] = useState<string | null>(null)
  const loadedRef = useRef<Set<string>>(new Set())

  const activeDiscussion = discussions.find((d) => d.id === activeDiscussionId) ?? null
  const activeMessages = activeDiscussionId ? messages[activeDiscussionId] ?? [] : []
  const isStreaming = activeDiscussionId ? streaming[activeDiscussionId] ?? false : false
  const isInterrupting = activeDiscussionId ? interrupting[activeDiscussionId] ?? false : false
  const isResumePending = activeDiscussionId ? resumePending[activeDiscussionId] ?? false : false
  const activePendingQuestion = activeDiscussionId ? pendingQuestions[activeDiscussionId] ?? null : null
  const activeQuestionOutcome = activeDiscussionId ? questionOutcomes[activeDiscussionId] ?? null : null

  const activeIdRef = useRef(activeDiscussionId)
  activeIdRef.current = activeDiscussionId
  const streamingRef = useRef(streaming)
  streamingRef.current = streaming
  // Synchronous view of the list for event handlers: reading status via a
  // setState updater's side effect is not reliable (updaters may run later).
  const discussionsRef = useRef(discussions)
  discussionsRef.current = discussions
  const pendingQuestionsRef = useRef(pendingQuestions)
  pendingQuestionsRef.current = pendingQuestions

  /**
   * Take down a discussion's question card. `outcome` records *why* it went —
   * a card torn down by the session dying was never answered, and labelling
   * that "Answered" is the bug this whole path exists to stop repeating.
   * Pass null when the history itself is gone and there is nothing to label.
   */
  const clearQuestion = useCallback((discussionId: string, outcome: QuestionOutcome | null) => {
    questionStatesRef.current = { ...questionStatesRef.current, [discussionId]: { pending: null, outcome } }
    setPendingQuestions((prev) => ({ ...prev, [discussionId]: null }))
    setQuestionOutcomes((prev) => ({ ...prev, [discussionId]: outcome }))
  }, [])

  /**
   * Put a discussion back into the "nothing is running" state. The three flags
   * move together — every terminal path already sets all three, and leaving one
   * behind is what strands the message queue (`resumePending` and a live
   * question are both drain vetoes in @redbamboo/chat's shouldDrain).
   */
  const clearStreamingLatch = useCallback((discussionId: string) => {
    setStreaming((prev) => ({ ...prev, [discussionId]: false }))
    setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
    setResumePending((prev) => ({ ...prev, [discussionId]: false }))
  }, [])

  // A local send latches `streaming` true optimistically, before the server has
  // had time to flip the session to Active. A reconcile landing inside that
  // window would read "not Active", clear the latch, and make the composer look
  // idle mid-turn — which is the exact state the message queue exists to avoid.
  // So a discussion that sent recently is left alone.
  const SEND_GRACE_MS = 10_000
  const lastSendAtRef = useRef<Record<string, number>>({})

  /**
   * Re-derive `streaming` from the server for anything still latched true.
   *
   * The flag is otherwise fed purely by pushed events, so a single one that
   * never arrives — a websocket that went stale while the tab was backgrounded,
   * or an event dropped at the `sessionToDiscussion` lookup because the
   * discussion list had not caught up with a new session id — leaves it stuck
   * true with nothing able to clear it. The composer then shows "Responding…"
   * over an idle session and the message queue holds indefinitely.
   *
   * Only ever clears. A turn this client did not start is announced by
   * `session.updated`, which is the path that sets the flag.
   */
  const reconcileStreaming = useCallback(async () => {
    if (!Object.values(streamingRef.current).some(Boolean)) return
    let active: Set<string>
    try {
      const list = await api.get<{ id: string; status: string }[]>("/ai-session/sessions?limit=200")
      active = new Set((list ?? []).filter((s) => s.status === "Active").map((s) => s.id))
    } catch { return }
    const now = Date.now()
    for (const d of discussionsRef.current) {
      if (!d.sessionId || !streamingRef.current[d.id]) continue
      if (active.has(d.sessionId)) continue
      if (now - (lastSendAtRef.current[d.id] ?? 0) < SEND_GRACE_MS) continue
      clearStreamingLatch(d.id)
    }
  }, [clearStreamingLatch])

  // The two moments this client has reason to distrust its own event history:
  // coming back to a tab that may have been suspended, and a socket that just
  // reconnected (handled in handleUpstreamReconnect).
  useEffect(() => {
    const onVisible = () => { if (document.visibilityState === "visible") reconcileStreaming() }
    document.addEventListener("visibilitychange", onVisible)
    window.addEventListener("focus", onVisible)
    return () => {
      document.removeEventListener("visibilitychange", onVisible)
      window.removeEventListener("focus", onVisible)
    }
  }, [reconcileStreaming])

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
      if ((disc?.type === "live" || disc?.type === "heartbeat") && disc?.sessionId) {
        // LIVE + heartbeat: merge session messages (chat) with Nova API messages
        // (events — tick digests are events in the heartbeat's stream)
        let sessionMsgs: MessageBlock[] = []
        try {
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[] }>(`/ai-session/sessions/${disc.sessionId}`)
          if (data.messages?.length) sessionMsgs = rebuildBlocks(data.messages)
        } catch {}

        let apiMsgs: MessageBlock[] = []
        try {
          const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}`)
          if (data.messages?.length) {
            // The API already merges session-transcript messages with event
            // messages, but we load the raw session above for full fidelity
            // (tool calls, thinking blocks, etc.). Only take event messages
            // from the API to avoid duplicates with the raw transcript.
            apiMsgs = toChatMessages(data.messages.filter((m) => m.source !== "session-transcript"))
          }
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
            const dedupContent = m.role === "user" ? stripContextXml(m.parts[0]?.content ?? "") : (m.parts[0]?.content ?? "")
            const normTs = m.timestamp.replace(/\+00:00$/, "Z")
            const key = `${normTs}:${dedupContent.slice(0, 50)}`
            if ((m.id != null && seen.has(m.id)) || seen.has(key)) return false
            if (m.id != null) seen.add(m.id)
            seen.add(key)
            return true
          })
          .sort(byTimestamp)

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

  const clearDiscussionSelection = useCallback(() => {
    setActiveDiscussionId(null)
  }, [])

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

  const createDiscussion = useCallback(async (agentId?: string, qualityTier?: string, provider?: string) => {
    setIsSpawning(true)
    try {
      const body: Record<string, string> = {}
      if (agentId) body.agentId = agentId
      if (qualityTier) body.qualityTier = qualityTier
      if (provider) body.provider = provider
      const d = await api.post<DiscussionInfo>("/api/apps/nova/discussions", Object.keys(body).length ? body : undefined)
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

    lastSendAtRef.current[discussionId] = Date.now()
    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
    setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
    setResumePending((prev) => ({ ...prev, [discussionId]: false }))
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
      const failStatus = disc.type === "live" ? "idle" as const : "stopped" as const
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: failStatus } : d)
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
    } catch (err) {
      if (err instanceof ApiError && err.status < 500) {
        fail()
        toast({ variant: "error", title: "Message not sent", description: err.message })
        return
      }
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
      } catch (err) {
        if (err instanceof ApiError && err.status < 500) {
          fail()
          toast({ variant: "error", title: "Message not sent", description: err.message })
          return
        }
        if (attempt < 2) {
          await new Promise((r) => setTimeout(r, 1000))
          continue
        }
        fail()
      }
    }
  }, [discussions, toast])

  const interruptDiscussion = useCallback(async (discussionId: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return
    try {
      const res = await api.post<{ interrupted: boolean; reason?: string }>(
        `/ai-session/sessions/${disc.sessionId}/interrupt`
      )
      // A refused interrupt ("NotActive") means the server has no running turn
      // — and it emits no stream event to say so, because from its side nothing
      // happened. `streaming` is a latch fed only by pushed events, so if one
      // was ever missed there is otherwise nothing left to clear it: the
      // composer shows "Responding…" over an idle session and the message queue
      // holds forever, with stop as the only escape hatch and stop doing
      // nothing. Believe the server and unlatch here.
      if (!res?.interrupted) clearStreamingLatch(discussionId)
    } catch { /* best effort */ }
  }, [discussions, clearStreamingLatch])

  /**
   * Two different things wear the name "answer" here.
   *
   * When the session is parked on an AskUserQuestion the CLI is blocked on a
   * control request, and only `/question` (echoing the live requestId from the
   * `question` stream event) unblocks it — a conversation turn would sit in the
   * queue behind the parked tool call. Without a requestId, either because the
   * backend never sent one or because the page was reloaded and the transient
   * event can't be replayed, `/answer` is still the right and only channel.
   */
  const answerQuestion = useCallback(async (discussionId: string, answer: string, payload?: QuestionAnswerPayload) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc?.sessionId) return
    const requestId = payload?.requestId ?? pendingQuestionsRef.current[discussionId]?.requestId ?? null
    clearQuestion(discussionId, "answered")
    lastSendAtRef.current[discussionId] = Date.now()
    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
    setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
    setResumePending((prev) => ({ ...prev, [discussionId]: false }))
    try {
      if (requestId) {
        // No payload means the caller had nothing structured to say (the
        // hands-free path speaks its answer) — that is the freeform channel.
        const body: Record<string, unknown> = { requestId }
        if (payload?.decline) { body.decline = true; body.reason = payload.reason }
        else if (payload?.answers?.length) body.answers = payload.answers
        else body.response = payload?.response ?? answer
        await api.post(`/ai-session/sessions/${disc.sessionId}/question`, body)
      } else {
        await api.post(`/ai-session/sessions/${disc.sessionId}/answer`, { answer })
      }
    } catch (err) {
      // 409 request_not_pending: the question timed out, was cancelled, or was
      // answered from another client while this card was still on screen. The
      // card is already gone, which is the correct outcome — the accompanying
      // question_resolved event carries the real reason. Nothing to report.
      if (err instanceof ApiError && err.status === 409) return
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
    }
  }, [discussions, clearQuestion])

  const resumeDiscussion = useCallback(async (discussionId: string) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc) return
    try {
      const result = await api.post<{ sessionId: string | null; status: "idle" }>(
        `/api/apps/nova/discussions/${discussionId}/resume`
      )
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId
          ? { ...d, sessionId: result.sessionId, status: result.status }
          : d)
      )
    } catch (err) {
      toast({
        variant: "error",
        title: "Failed to restart discussion",
        description: err instanceof Error ? err.message : "Unknown error",
      })
    }
  }, [discussions, toast])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "session.updated") {
      const session = event.data as { id: string; status: string; title?: string }
      const discId = sessionToDiscussion.get(session.id)
      if (!discId) return
      if (session.status !== "Active") {
        setStreaming((prev) => ({ ...prev, [discId]: false }))
        setInterrupting((prev) => ({ ...prev, [discId]: false }))
        setResumePending((prev) => ({ ...prev, [discId]: false }))
        // Closed (archived/archiving) discussions are terminal: never echo
        // session events back to the server for them. Checked via the ref —
        // a setState updater's side effect is not guaranteed to have run here.
        const known = discussionsRef.current.find((d) => d.id === discId)
        if (!known || isClosed(known.status) || pendingArchivesRef.current.has(discId)) return
        const isStopped = session.status === "Stopped" || session.status === "Error"
        const isLiveDisc = known.type === "live"
        const discStatus = (isStopped && !isLiveDisc) ? "stopped" as const : "idle" as const
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
        if (isStopped && !isLiveDisc) {
          api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
        } else {
          api.put(`/api/apps/nova/discussions/${discId}/activity`).catch(() => {})
          if (isViewing) {
            api.put(`/api/apps/nova/discussions/${discId}/read`).catch(() => {})
          }
          // LIVE and heartbeat discussions own their titles — don't let
          // session auto-titles overwrite them (the session title drifts to
          // whatever topic was last discussed, which is confusing).
          if (!isLiveDisc && known.type !== "heartbeat") {
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
      } else if (!pendingQuestionsRef.current[discId]) {
        // Active means a turn is running even when this client didn't start
        // it — an automation, the heartbeat, or the same discussion open on
        // his phone. Without this there is no path back to streaming=true, so
        // the composer looks idle and the message queue drains straight into a
        // live turn. A pending question is the exception: the turn is Active
        // but blocked on the user, and answers go through onAnswerQuestion.
        setStreaming((prev) => ({ ...prev, [discId]: true }))
      }
    } else if (event.type === "session.ended") {
      const { id } = event.data as { id: string }
      const discId = sessionToDiscussion.get(id)
      if (!discId) return
      setStreaming((prev) => ({ ...prev, [discId]: false }))
      // A card still up when the session died was never answered.
      clearQuestion(discId, pendingQuestionsRef.current[discId] ? "session_ended" : questionStatesRef.current[discId]?.outcome ?? null)
      setInterrupting((prev) => ({ ...prev, [discId]: false }))
      setResumePending((prev) => ({ ...prev, [discId]: false }))
      const known = discussionsRef.current.find((d) => d.id === discId)
      const closed = !known || isClosed(known.status) || pendingArchivesRef.current.has(discId)
      const isLive = known?.type === "live"
      setDiscussions((prev) =>
        prev.map((d) => d.id === discId && !isClosed(d.status) ? { ...d, status: isLive ? "idle" as const : "stopped" as const } : d)
      )
      if (!closed && !isLive) api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
    } else if (event.type === "discussion.created") {
      const { discussionId, agentId, status, type } = event.data as { discussionId: string; agentId?: string; status?: string; type?: string }
      if (!discussionId) return
      setDiscussions((prev) => {
        if (prev.some((d) => d.id === discussionId)) return prev
        const newDisc: DiscussionInfo = {
          id: discussionId,
          // The WS event doesn't carry the entity id; share stays disabled for
          // this placeholder until the next discussions refresh fills it in.
          entityId: "",
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
      const { discussionId, content, source, senderAgentId, metadata, timestamp: serverTs } = event.data as { discussionId: string; sessionId: string; content: string; source: string; senderAgentId?: string; metadata?: Record<string, unknown> | null; timestamp?: string }
      if (!discussionId) return
      const ts = serverTs ?? new Date().toISOString()
      setMessages((prev) => ({
        ...prev,
        [discussionId]: appendEvent(prev[discussionId] ?? [], {
          source: source ? `event:${source}` : "event:system",
          content,
          data: metadata ?? null,
          timestamp: ts,
          senderAgentId,
        }, eventResolver),
      }))
    } else if (event.type === "discussion.nova-message") {
      const { discussionId, content, audioUrl, senderAgentId, timestamp: serverTs } = event.data as { discussionId: string; content: string; audioUrl?: string; senderAgentId?: string; timestamp?: string }
      if (!discussionId) return
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const parts: import("@redbamboo/chat").MessagePart[] = [{ type: "text", content }]
        if (audioUrl) parts.push({ type: "audio", content: audioUrl })
        const newBlock: import("@redbamboo/chat").MessageBlock = {
          id: `nova-msg-${Date.now()}`,
          role: "assistant",
          parts,
          timestamp: serverTs ?? new Date().toISOString(),
          senderAgentId,
        }
        return { ...prev, [discussionId]: [...current, newBlock].sort(byTimestamp) }
      })
    } else if (event.type === "discussion.cleared") {
      const { discussionId } = event.data as { discussionId: string }
      if (!discussionId) return
      setMessages((prev) => ({ ...prev, [discussionId]: [] }))
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
      clearQuestion(discussionId, null)
      setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
      setResumePending((prev) => ({ ...prev, [discussionId]: false }))
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

      // Copied field by field rather than spread, so keep this in step with
      // ChatEvent: anything missed here is silently dropped, and `requestId` in
      // particular is the only handle on a parked question — without it the
      // answer has nothing to echo back and the session stays blocked.
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
        requestId: evt.requestId ?? null,
      }

      setMessages((prev) => {
        const current = prev[discId] ?? []
        const result = processStreamEvent(current, true, chatEvent, resumePendingRef.current[discId] ?? false, questionStatesRef.current[discId])
        setStreaming((p) => ({ ...p, [discId]: result.isStreaming }))
        setInterrupting((p) => ({ ...p, [discId]: result.interrupting }))
        setResumePending((p) => ({ ...p, [discId]: result.resumePending }))
        // The lifecycle is owned by processStreamEvent now — it knows that
        // "interrupting" is transitional and must not drop a live question, and
        // that only a question_resolved says how one actually ended. Mirror it
        // wholesale rather than second-guessing it here.
        questionStatesRef.current = { ...questionStatesRef.current, [discId]: result.question }
        setPendingQuestions((p) => ({ ...p, [discId]: result.question.pending }))
        setQuestionOutcomes((p) => ({ ...p, [discId]: result.question.outcome }))
        // "killed" is terminal but NOT safe/idle yet — the process is being
        // replaced, and the discussion list must not advertise it as available
        // until the follow-up status says so.
        if (chatEvent.type === "status" && chatEvent.content !== "interrupting"
            && !result.isStreaming && !result.resumePending) {
          setDiscussions((p) =>
            p.map((d) => d.id === discId ? { ...d, status: "idle" as const } : d)
          )
        }
        return { ...prev, [discId]: result.messages }
      })
    }
  }, [sessionToDiscussion, clearQuestion])

  const handleUpstreamDisconnect = useCallback(() => {
    setUpstreamConnected(false)
    setStreaming({})
    questionStatesRef.current = {}
    setPendingQuestions({})
    setQuestionOutcomes({})
    setInterrupting({})
    setResumePending({})
  }, [])

  const handleUpstreamReconnect = useCallback(() => {
    setUpstreamConnected(true)
    refreshDiscussions()
    reloadActiveMessages(true)
    // Anything that stayed latched while the socket was down has to be settled
    // against the server: the events that would have cleared it were emitted
    // into a connection nobody was holding.
    reconcileStreaming()
  }, [refreshDiscussions, reloadActiveMessages, reconcileStreaming])

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
      const disc = discussions.find((d) => d.id === id)
      if (disc?.type === "heartbeat" && disc.agentId) {
        await api.post(`/api/apps/nova/heartbeat/${disc.agentId}/rotate`)
      } else {
        await api.post<{ archived: DiscussionInfo; created: DiscussionInfo }>(`/api/apps/nova/discussions/${id}/rotate`)
      }
      setDiscussions((prev) => prev.filter((d) => d.id !== id))
      setMessages((prev) => { const next = { ...prev }; delete next[id]; return next })
      loadedRef.current.delete(id)
      const label = disc?.type === "heartbeat" ? "Heartbeat" : "LIVE"
      toast({ variant: "success", title: `${label} rotated`, description: `Fresh ${label} discussion created` })
    } catch (err) {
      toast({ variant: "error", title: "Failed to rotate", description: err instanceof Error ? err.message : "Unknown error" })
    }
  }, [toast, discussions])

  const renameDiscussion = useCallback(async (id: string, title: string) => {
    await api.put(`/api/apps/nova/discussions/${id}/title`, { title })
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, title } : d))
  }, [])

  const setConfidential = useCallback(async (id: string, confidential: boolean) => {
    await api.put(`/api/apps/nova/discussions/${id}/confidential`, { confidential })
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, confidential } : d))
  }, [])

  return {
    discussions: visibleDiscussions,
    activeDiscussion,
    activeDiscussionId,
    activeMessages,
    isStreaming,
    isInterrupting,
    isResumePending,
    isSpawning,
    pendingQuestion: activePendingQuestion,
    questionOutcome: activeQuestionOutcome,
    selectDiscussion,
    clearDiscussionSelection,
    createDiscussion,
    sendMessage,
    interruptDiscussion,
    answerQuestion,
    archiveDiscussion,
    rotateDiscussion,
    dismissDiscussion,
    renameDiscussion,
    setConfidential,
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
