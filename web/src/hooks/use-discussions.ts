import { useState, useCallback, useEffect, useRef, useMemo, useSyncExternalStore, startTransition } from "react"
import { useToast, useUiEnvironment } from "@redbamboo/ui"
import { api, ApiError } from "../lib/api"
import type { DiscussionHistoryOverlay, DiscussionHistoryPageResponse, DiscussionInfo, DiscussionMessage, ClaudeStreamEvent, WsEvent, EventType } from "../lib/types"
import type { ChatInputPart, MessageBlock, MessagePart, PendingQuestion, QuestionAnswerPayload, QuestionOutcome, QuestionState, ChatEvent, ImageAttachment, PersistedTranscriptPage, SendOptions, TranscriptCursor, UploadedAttachment } from "@redbamboo/chat"
import { DurableTranscriptPager, processStreamEvent, rebuildBlocks, TranscriptAccumulator } from "@redbamboo/chat"
import type { PersistedMessage } from "@redbamboo/chat"
import { appendEvent, byTimestamp, isRawEventMessage, orderMessages } from "../lib/message-order"
import { accumulateHistoryOverlays, coalesceDiscussionTurnBlocks, filterInternalBootstrapBlock, mergeDiscussionAndSessionBlocks, mergeNovaMessageArrival, mergePagedDiscussionAndSessionBlocks } from "../lib/discussion-transcript"
import { applySessionStatus, applySettledSessionStatus, preservesRecentStreamingLatch, shouldRequestSessionTitleSync } from "../lib/discussion-runtime"
import { resolveRotatedDiscussionSelection } from "../lib/discussion-rotation"
import { applyConversationMessageArrival, applyDiscussionMessageArrival } from "../lib/discussion-unread"
import { HistoryLifecycleTombstones, historyRevalidationDirection, invalidateHistoryGeneration, isCurrentHistoryGeneration, shouldAccumulatePushedHistoryOverlay, shouldCatchUpHistory } from "../lib/discussion-history-page"
import { LatestTaskCoordinator } from "../lib/latest-task-coordinator"
import { DeferredInvalidationCoordinator } from "../lib/deferred-invalidation-coordinator"
import {
  clearDiscussionArchivePending,
  getDiscussionList,
  isDiscussionArchivePending,
  markDiscussionArchivePending,
  setDiscussionList,
  subscribeDiscussionList,
  upsertDiscussion,
} from "../lib/discussion-list-store"

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

const HISTORY_PAGE_LIMIT = 500
const INITIAL_HISTORY_TAIL = 500
const HISTORY_TAIL_STEP = 500
const MAX_HISTORY_TAIL = 10_000
// RedCompute batches ordinary transcript records to RedLeaf every 500 ms.
// Revalidate after that durability window instead of treating the first Idle
// snapshot as fully flushed.
const SETTLED_TRANSCRIPT_RELOAD_DELAY_MS = 750

type HistoryMode = "v2" | "legacy"

function isHistoryPageResponse(value: unknown): value is DiscussionHistoryPageResponse {
  if (!value || typeof value !== "object") return false
  const candidate = value as Partial<DiscussionHistoryPageResponse>
  const page = candidate.page
  return !!candidate.discussion
    && (candidate.session === null || (!!candidate.session && typeof candidate.session.id === "string"))
    && Array.isArray(candidate.messages)
    && Array.isArray(candidate.overlays)
    && !!page
    && (page.direction === "newest" || page.direction === "before" || page.direction === "after")
    && (page.epoch === null || typeof page.epoch === "string")
    && (page.oldestCursor === null || typeof page.oldestCursor === "string")
    && (page.newestCursor === null || typeof page.newestCursor === "string")
    && typeof page.hasEarlier === "boolean"
    && typeof page.hasLater === "boolean"
    && typeof page.boundaryComplete === "boolean"
}

function toPersistedTranscriptPage(data: DiscussionHistoryPageResponse): PersistedTranscriptPage {
  return {
    epoch: data.page.epoch,
    records: data.messages,
    oldestCursor: data.page.oldestCursor,
    newestCursor: data.page.newestCursor,
    hasEarlier: data.page.hasEarlier,
    hasLater: data.page.hasLater,
    fromSequence: data.page.fromSequence,
    throughSequence: data.page.throughSequence,
    boundaryComplete: data.page.boundaryComplete,
  }
}

function historyResponseSessionId(data: DiscussionHistoryPageResponse): string | null {
  return data.discussion.sessionId ?? data.session?.id ?? null
}

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
  const blocks = messages.map((m) => {
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
    if (m.messageUid) metadata.messageUid = m.messageUid
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
          attachments: p.attachments,
          payloadRef: p.payloadRef,
          phase: p.phase,
        })),
      timestamp: m.timestamp,
      senderAgentId: m.senderAgentId,
      metadata: Object.keys(metadata).length > 0 ? metadata : undefined,
    }
  })
  return coalesceDiscussionTurnBlocks(blocks)
}

export function useDiscussions(eventResolver?: EventResolver) {
  const { toast } = useToast()
  const environment = useUiEnvironment()
  // Float Nova and the ordinary Nova route are separate React trees. The list
  // is server state, so both trees observe one module-level snapshot while all
  // view state below (selection, loaded transcript, dialogs) remains local.
  const discussions = useSyncExternalStore(subscribeDiscussionList, getDiscussionList, getDiscussionList)
  const setDiscussions = setDiscussionList
  const [activeDiscussionId, setActiveDiscussionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, MessageBlock[]>>({})
  const transcriptAccumulatorsRef = useRef(new Map<string, TranscriptAccumulator>())
  const durableTranscriptPagersRef = useRef(new Map<string, DurableTranscriptPager>())
  const historyOverlaysRef = useRef(new Map<string, Map<string, DiscussionHistoryOverlay>>())
  const historySessionIdsRef = useRef(new Map<string, string | null>())
  const historyModesRef = useRef(new Map<string, HistoryMode>())
  const historyHardResetRef = useRef(new Set<string>())
  const historyLifecycleRef = useRef(new HistoryLifecycleTombstones())
  const snapshotCoordinatorRef = useRef(new LatestTaskCoordinator<string>())
  const transcriptAccumulator = useCallback((discussionId: string) => {
    let accumulator = transcriptAccumulatorsRef.current.get(discussionId)
    if (!accumulator) {
      accumulator = new TranscriptAccumulator()
      transcriptAccumulatorsRef.current.set(discussionId, accumulator)
    }
    return accumulator
  }, [])
  const durableTranscriptPager = useCallback((discussionId: string) => {
    let pager = durableTranscriptPagersRef.current.get(discussionId)
    if (!pager) {
      pager = new DurableTranscriptPager()
      durableTranscriptPagersRef.current.set(discussionId, pager)
    }
    return pager
  }, [])
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
  const [loadingEarlierDiscussionId, setLoadingEarlierDiscussionId] = useState<string | null>(null)
  const loadingEarlierRef = useRef(new Set<string>())
  const [hasEarlierMessages, setHasEarlierMessages] = useState<Record<string, boolean>>({})
  const loadedRef = useRef<Set<string>>(new Set())
  // Exists only for old servers that do not expose history-page. V2 never
  // derives pagination state from an accumulating tail count.
  const legacyHistoryTailRef = useRef<Record<string, number>>({})
  const loadGenerationRef = useRef<Record<string, number>>({})
  const activeObservedAfterSendRef = useRef<Record<string, boolean>>({})
  const sessionUpdateGenerationRef = useRef<Record<string, number>>({})
  const handleWsEventRef = useRef<((event: WsEvent) => void) | null>(null)
  const confidentialInvalidations = useMemo(() => new DeferredInvalidationCoordinator<string>({
    setTimeout: (callback, delayMs) => environment.window.setTimeout(callback, delayMs),
    clearTimeout: (handle) => environment.window.clearTimeout(handle),
  }, SETTLED_TRANSCRIPT_RELOAD_DELAY_MS), [environment.window])

  useEffect(() => () => confidentialInvalidations.clear(), [confidentialInvalidations])

  const activeDiscussion = discussions.find((d) => d.id === activeDiscussionId) ?? null
  const activeMessages = activeDiscussionId ? messages[activeDiscussionId] ?? [] : []
  const isStreaming = activeDiscussionId ? streaming[activeDiscussionId] ?? false : false
  const isInterrupting = activeDiscussionId ? interrupting[activeDiscussionId] ?? false : false
  const isResumePending = activeDiscussionId ? resumePending[activeDiscussionId] ?? false : false
  const activePendingQuestion = activeDiscussionId ? pendingQuestions[activeDiscussionId] ?? null : null
  const activeQuestionOutcome = activeDiscussionId ? questionOutcomes[activeDiscussionId] ?? null : null

  const activeIdRef = useRef(activeDiscussionId)
  activeIdRef.current = activeDiscussionId
  const messagesRef = useRef(messages)
  messagesRef.current = messages
  const streamingRef = useRef(streaming)
  streamingRef.current = streaming
  const latchStreaming = useCallback((discussionId: string) => {
    streamingRef.current = { ...streamingRef.current, [discussionId]: true }
    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
  }, [])
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
    streamingRef.current = { ...streamingRef.current, [discussionId]: false }
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
    const onVisible = () => { if (environment.document.visibilityState === "visible") reconcileStreaming() }
    environment.document.addEventListener("visibilitychange", onVisible)
    environment.window.addEventListener("focus", onVisible)
    return () => {
      environment.document.removeEventListener("visibilitychange", onVisible)
      environment.window.removeEventListener("focus", onVisible)
    }
  }, [environment.document, environment.window, reconcileStreaming])

  const sessionToDiscussion = useMemo(() => {
    const map = new Map<string, string>()
    for (const d of discussions) {
      if (d.sessionId) map.set(d.sessionId, d.id)
    }
    return map
  }, [discussions])

  const refreshDiscussions = useCallback(async () => {
    const list = await api.get<DiscussionInfo[]>("/api/apps/nova/discussions")
    setDiscussions(list)
  }, [])

  const syncAndRefresh = useCallback(async () => {
    await api.post("/api/apps/nova/discussions/sync").catch(() => {})
    await refreshDiscussions()
  }, [refreshDiscussions])

  const acknowledgeRead = useCallback(async (id: string, conversationRevision: number) => {
    try {
      const updated = await api.put<DiscussionInfo>(`/api/apps/nova/discussions/${id}/read`, {
        conversationRevision,
      })
      upsertDiscussion(updated)
    } catch { /* a later refresh preserves any still-unread revision */ }
  }, [])

  useEffect(() => {
    syncAndRefresh()
  }, [syncAndRefresh])

  const resetHistoryPaging = useCallback((id: string, forgetCapability = false) => {
    // Invalidate any response already in flight before clearing its anchors.
    invalidateHistoryGeneration(loadGenerationRef.current, id)
    durableTranscriptPagersRef.current.get(id)?.reset()
    historyOverlaysRef.current.delete(id)
    historySessionIdsRef.current.delete(id)
    historyHardResetRef.current.add(id)
    transcriptAccumulatorsRef.current.get(id)?.reset()
    legacyHistoryTailRef.current[id] = INITIAL_HISTORY_TAIL
    if (forgetCapability) historyModesRef.current.delete(id)
    setHasEarlierMessages((prev) => ({ ...prev, [id]: false }))
  }, [])

  const retireHistoryPaging = useCallback((id: string) => {
    historyLifecycleRef.current.retire(id)
    invalidateHistoryGeneration(loadGenerationRef.current, id)
    transcriptAccumulatorsRef.current.get(id)?.reset()
    durableTranscriptPagersRef.current.get(id)?.reset()
    transcriptAccumulatorsRef.current.delete(id)
    durableTranscriptPagersRef.current.delete(id)
    historyOverlaysRef.current.delete(id)
    historySessionIdsRef.current.delete(id)
    historyModesRef.current.delete(id)
    historyHardResetRef.current.delete(id)
    delete legacyHistoryTailRef.current[id]
    loadedRef.current.delete(id)
  }, [])

  const commitHistoryPage = useCallback((
    id: string,
    generation: number,
    data: DiscussionHistoryPageResponse,
    direction: "newest" | "before" | "after",
    expectedCursor?: string | null,
  ): boolean => {
    // Rotation/archive/clear leave a monotonic tombstone. Reject the old page
    // before it can mutate pager state, overlays, metadata, or visible blocks.
    if (!historyLifecycleRef.current.canRun(id)
      || !isCurrentHistoryGeneration(loadGenerationRef.current, id, generation))
      return false
    if (data.session && data.discussion.sessionId && data.session.id !== data.discussion.sessionId)
      return false
    // An overlay-only older page can omit session metadata while its outer
    // cursor still belongs to the discussion's linked session.
    const responseSessionId = historyResponseSessionId(data)
    const hadSessionIdentity = historySessionIdsRef.current.has(id)
    const previousSessionId = historySessionIdsRef.current.get(id) ?? null
    const sessionChanged = hadSessionIdentity && previousSessionId !== responseSessionId
    const hardResetPending = historyHardResetRef.current.has(id)
    const pager = durableTranscriptPager(id)
    const accumulator = transcriptAccumulator(id)

    // An older/after response belongs to the anchor that requested it. If the
    // discussion rotated to another session in flight, discard it and let the
    // caller recover from the newest page.
    if (sessionChanged) {
      if (direction !== "newest") return false
      pager.reset()
      accumulator.reset()
      historyOverlaysRef.current.delete(id)
      pager.startNewestPage(generation)
      accumulator.startSnapshot(generation)
    }

    const previousEpoch = pager.current().epoch
    const page = toPersistedTranscriptPage(data)
    const result = direction === "newest"
      ? pager.commitNewestPage(generation, page)
      : direction === "before"
        ? pager.prependOlderPage(generation, expectedCursor ?? null, page)
        : pager.appendNewerPage(generation, expectedCursor ?? null, page)
    if (!result.accepted) return false

    const epochChanged = previousEpoch !== null && previousEpoch !== result.epoch
    if (epochChanged)
      historyOverlaysRef.current.delete(id)
    const overlays = accumulateHistoryOverlays(
      historyOverlaysRef.current.get(id) ?? new Map(),
      data.overlays,
    )
    historyOverlaysRef.current.set(id, overlays)
    historySessionIdsRef.current.set(id, responseSessionId)
    historyHardResetRef.current.delete(id)
    historyModesRef.current.set(id, "v2")
    setHasEarlierMessages((prev) =>
      historyLifecycleRef.current.canRun(id)
        && isCurrentHistoryGeneration(loadGenerationRef.current, id, generation)
        ? { ...prev, [id]: result.hasEarlier }
        : prev)
    if (!historyLifecycleRef.current.canRun(id)
      || !isCurrentHistoryGeneration(loadGenerationRef.current, id, generation))
      return false
    upsertDiscussion(data.discussion)

    const sessionBlocks = filterInternalBootstrapBlock(
      rebuildBlocks(result.records),
      data.discussion.setupBootstrapMessageUid,
    )
    const overlayBlocks = toChatMessages([...overlays.values()])
    const authoritative = cleanMessages(
      mergePagedDiscussionAndSessionBlocks(overlayBlocks, sessionBlocks),
      eventResolver,
    )
    const cursor: TranscriptCursor | null = result.epoch && result.throughSequence !== null
      ? { epoch: result.epoch, throughSequence: result.throughSequence }
      : null

    setMessages((prev) => {
      if (!historyLifecycleRef.current.canRun(id)
        || loadGenerationRef.current[id] !== generation) return prev
      const reconciled = accumulator.commitSnapshot(generation, authoritative, cursor).messages
      const durableIds = new Set(reconciled.map(message => message.id))
      // Keep only locally-created blocks with a stable id while their durable
      // bridge is in flight. No V2 path compares message content or timestamps.
      const transient = hardResetPending || sessionChanged || epochChanged
        ? []
        : (prev[id] ?? []).filter(message =>
            !durableIds.has(message.id)
            && (message.role === "user" || message.id.startsWith("error-")))
      return { ...prev, [id]: [...reconciled, ...transient].sort(byTimestamp) }
    })
    return true
  }, [durableTranscriptPager, eventResolver, transcriptAccumulator])

  const loadMessagesUncoalesced = useCallback(async (
    id: string,
    tail = INITIAL_HISTORY_TAIL,
    force = false,
    sessionIdOverride?: string | null,
    preferDiscussionApi = false,
  ) => {
    if (!historyLifecycleRef.current.canRun(id)) return
    if (!force && loadedRef.current.has(id)) return

    const currentDiscussion = discussionsRef.current.find((d) => d.id === id)
    if (!currentDiscussion) return
    // An accepted-message event can beat the POST response that updates the
    // shared discussion record with a newly-created session id. Carrying the
    // event's id into this revalidation avoids a timing-dependent empty load.
    const disc = sessionIdOverride && sessionIdOverride !== currentDiscussion.sessionId
      ? { ...currentDiscussion, sessionId: sessionIdOverride }
      : currentDiscussion

    setLoadingDiscussionId(id)
    loadedRef.current.add(id)
    const generation = (loadGenerationRef.current[id] ?? 0) + 1
    loadGenerationRef.current[id] = generation
    const accumulator = transcriptAccumulator(id)
    accumulator.startSnapshot(generation)
    const isCurrentLoad = () => loadGenerationRef.current[id] === generation
    const commitMessages = (authoritative: MessageBlock[], cursor?: TranscriptCursor | null) => {
      if (!isCurrentLoad()) return
      setMessages((prev) => {
        if (!isCurrentLoad()) return prev
        const current = prev[id] ?? []
        const reconciled = accumulator.commitSnapshot(generation, authoritative, cursor).messages
        const reconciledIds = new Set(reconciled.map(message => message.id))
        const authoritativeUserContent = new Set(reconciled
          .filter(message => message.role === "user")
          .map(message => stripContextXml(message.parts[0]?.content ?? "")))
        // Product-local overlays are not transcript arbitration: ambient Nova
        // events and newly admitted user/error blocks can race the HTTP read.
        const overlays = current.filter(message => !reconciledIds.has(message.id) && (
          (typeof message.metadata?.source === "string" && message.metadata.source.startsWith("event:"))
          || (message.role === "user"
            && !authoritativeUserContent.has(stripContextXml(message.parts[0]?.content ?? "")))
          || message.id.startsWith("error-")
        ))
        const merged = [...reconciled, ...overlays].sort(byTimestamp)
        return { ...prev, [id]: merged }
      })
    }
    const commitHistory = (hasEarlier: boolean) => {
      if (!isCurrentLoad()) return
      legacyHistoryTailRef.current[id] = tail
      setHasEarlierMessages((prev) => ({ ...prev, [id]: hasEarlier }))
    }
    try {
      if (historyModesRef.current.get(id) !== "legacy") {
        const pager = durableTranscriptPager(id)
        pager.startNewestPage(generation)
        try {
          const data = await api.get<unknown>(
            `/api/apps/nova/discussions/${encodeURIComponent(id)}/history-page?limit=${HISTORY_PAGE_LIMIT}`,
          )
          if (!isHistoryPageResponse(data)) {
            // A 200 response without the V2 envelope is a capability mismatch,
            // not a partially valid page. Fall back as one complete legacy path.
            historyModesRef.current.set(id, "legacy")
            historyOverlaysRef.current.delete(id)
          } else {
            if (!commitHistoryPage(id, generation, data, "newest"))
              loadedRef.current.delete(id)
            return
          }
        } catch (error) {
          if (error instanceof ApiError && error.status === 404) {
            historyModesRef.current.set(id, "legacy")
            historyOverlaysRef.current.delete(id)
          } else {
            // V2 server errors preserve the loaded window. Never reinterpret a
            // failed cursor/page response as an old rolling-tail snapshot.
            loadedRef.current.delete(id)
            return
          }
        }
      }

      if ((disc?.type === "live" || disc?.type === "heartbeat") && disc?.sessionId) {
        // LIVE + heartbeat: merge session messages (chat) with Nova API messages
        // (events — tick digests are events in the heartbeat's stream)
        let sessionMsgs: MessageBlock[] = []
        let sessionHasEarlier = false
        let sessionCursor: TranscriptCursor | null = null
        try {
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[]; transcript?: TranscriptCursor }>(`/ai-session/sessions/${disc.sessionId}?tail=${tail}`)
          if (data.messages?.length) {
            sessionMsgs = filterInternalBootstrapBlock(
              rebuildBlocks(data.messages),
              disc.setupBootstrapMessageUid,
            )
            sessionHasEarlier = data.messages.length >= tail
            sessionCursor = data.transcript ?? null
          }
        } catch {}

        let apiMsgs: MessageBlock[] = []
        let apiHasEarlier = false
        try {
          const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}?tail=${tail}`)
          if (data.messages?.length) {
            apiHasEarlier = data.messages.length >= tail
            apiMsgs = toChatMessages(data.messages)
          }
        } catch {}

        // Nova API messages first: when both stores hold the same logical
        // message (shared uid), the Nova copy wins dedup — it carries the
        // source metadata that drives event rendering. Order on screen is
        // unaffected (the merge re-sorts by timestamp below).
        const merged = mergeDiscussionAndSessionBlocks(apiMsgs, sessionMsgs, stripContextXml)

        commitMessages(cleanMessages(merged, eventResolver), sessionCursor)
        commitHistory(sessionHasEarlier || apiHasEarlier)
        return
      }

      // An accepted user-message event is published only after Nova has stored
      // its canonical bridge record. Read that endpoint first for convergence:
      // the raw Compute transcript can legitimately lag the accepted send.
      if (preferDiscussionApi) {
        try {
          const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}?tail=${tail}`)
          let sessionMsgs: MessageBlock[] = []
          let sessionHasEarlier = false
          let sessionCursor: TranscriptCursor | null = null
          if (disc.sessionId) {
            try {
              const session = await api.get<{ session: { title?: string }; messages: PersistedMessage[]; transcript?: TranscriptCursor }>(`/ai-session/sessions/${disc.sessionId}?tail=${tail}`)
              if (session.messages?.length) {
                sessionMsgs = filterInternalBootstrapBlock(
                  rebuildBlocks(session.messages),
                  disc.setupBootstrapMessageUid,
                )
                sessionHasEarlier = session.messages.length >= tail
                sessionCursor = session.transcript ?? null
              }
            } catch {
              // The authorized discussion projection remains a safe fallback.
            }
          }
          const merged = mergeDiscussionAndSessionBlocks(
            toChatMessages(data.messages ?? []),
            sessionMsgs,
            stripContextXml,
          )
          commitMessages(cleanMessages(merged, eventResolver), sessionCursor)
          commitHistory(data.messages.length >= tail || sessionHasEarlier)
          return
        } catch {
          // Preserve the ordinary raw-session fallback if the canonical read
          // itself is temporarily unavailable.
        }
      }

      if (disc?.sessionId) {
        try {
          const data = await api.get<{ session: { title?: string }; messages: PersistedMessage[]; transcript?: TranscriptCursor }>(`/ai-session/sessions/${disc.sessionId}?tail=${tail}`)
          if (data.messages?.length) {
            const sessionMsgs = filterInternalBootstrapBlock(
              rebuildBlocks(data.messages),
              disc.setupBootstrapMessageUid,
            )
            let discussionMsgs: MessageBlock[] = []
            let discussionHasEarlier = false
            try {
              const projected = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}?tail=${tail}`)
              discussionMsgs = toChatMessages(projected.messages ?? [])
              discussionHasEarlier = projected.messages.length >= tail
            } catch {
              // Raw session history remains authoritative when the discussion
              // projection is temporarily unavailable.
            }
            const merged = mergeDiscussionAndSessionBlocks(
              discussionMsgs,
              sessionMsgs,
              stripContextXml,
            )
            commitMessages(cleanMessages(merged, eventResolver), data.transcript)
            commitHistory(data.messages.length >= tail || discussionHasEarlier)
            return
          }
        } catch {
          // A failed history read says nothing about provider lifecycle. Auth,
          // network, and overloaded-store failures must not fan out into
          // session resumes. Fall through to Nova's durable projection;
          // stopped sessions resume only through the explicit lifecycle action.
        }
      }

      try {
        const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/apps/nova/discussions/${id}?tail=${tail}`)
        commitMessages(cleanMessages(toChatMessages(data.messages ?? []), eventResolver))
        commitHistory(data.messages.length >= tail)
      } catch { /* discussion not found */ }
    } finally {
      if (isCurrentLoad()) setLoadingDiscussionId((cur) => cur === id ? null : cur)
    }
  }, [commitHistoryPage, durableTranscriptPager, eventResolver, transcriptAccumulator])

  const loadMessages = useCallback((
    id: string,
    tail = INITIAL_HISTORY_TAIL,
    force = false,
    sessionIdOverride?: string | null,
    preferDiscussionApi = false,
  ) => snapshotCoordinatorRef.current.run(id, () =>
    loadMessagesUncoalesced(id, tail, force, sessionIdOverride, preferDiscussionApi),
    force,
  ), [loadMessagesUncoalesced])

  /** Catch up from the retained newest outer cursor without replacing history. */
  const catchUpMessages = useCallback((
    id: string,
    sessionIdOverride?: string | null,
    preferDiscussionApi = false,
  ) => {
    if (!historyLifecycleRef.current.canRun(id)) return Promise.resolve()
    const pager = durableTranscriptPagersRef.current.get(id)
    if (!pager || historyRevalidationDirection(
      historyModesRef.current.get(id),
      pager?.current().newestCursor,
    ) !== "after") {
      const tail = legacyHistoryTailRef.current[id] ?? INITIAL_HISTORY_TAIL
      return loadMessages(id, tail, true, sessionIdOverride, preferDiscussionApi)
    }
    const activePager = pager

    return snapshotCoordinatorRef.current.run(id, async () => {
      if (!historyLifecycleRef.current.canRun(id)) return
      loadedRef.current.add(id)
      while (true) {
        const requestId = (loadGenerationRef.current[id] ?? 0) + 1
        loadGenerationRef.current[id] = requestId
        const expectedCursor = activePager.startNewerPage(requestId)
        if (!expectedCursor) return
        transcriptAccumulator(id).startSnapshot(requestId)
        try {
          const data = await api.get<unknown>(
            `/api/apps/nova/discussions/${encodeURIComponent(id)}/history-page?limit=${HISTORY_PAGE_LIMIT}&after=${encodeURIComponent(expectedCursor)}`,
          )
          if (!isHistoryPageResponse(data)) {
            loadedRef.current.delete(id)
            return
          }
          if (historySessionIdsRef.current.has(id)
            && historySessionIdsRef.current.get(id) !== historyResponseSessionId(data)) {
            resetHistoryPaging(id)
            loadedRef.current.delete(id)
            void loadMessages(id, INITIAL_HISTORY_TAIL, true, sessionIdOverride)
            return
          }
          if (!commitHistoryPage(id, requestId, data, "after", expectedCursor)) {
            loadedRef.current.delete(id)
            return
          }
          if (!activePager.current().hasLater) return
        } catch (error) {
          loadedRef.current.delete(id)
          if (error instanceof ApiError && error.status === 409
            && (error.code === "history_cursor_stale" || error.code === "history_cursor_mismatch")) {
            resetHistoryPaging(id)
            // Queue a fresh newest page behind this stale anchored request.
            void loadMessages(id, INITIAL_HISTORY_TAIL, true, sessionIdOverride)
          }
          return
        }
      }
    }, true)
  }, [commitHistoryPage, loadMessages, resetHistoryPaging, transcriptAccumulator])

  const loadEarlierMessages = useCallback(async (id: string) => {
    if (!historyLifecycleRef.current.canRun(id)) return
    if (loadingEarlierRef.current.has(id) || !hasEarlierMessages[id]) return
    loadingEarlierRef.current.add(id)
    setLoadingEarlierDiscussionId(id)
    try {
      if (historyModesRef.current.get(id) === "v2") {
        await snapshotCoordinatorRef.current.run(id, async () => {
          if (!historyLifecycleRef.current.canRun(id)) return
          const pager = durableTranscriptPager(id)
          const requestId = (loadGenerationRef.current[id] ?? 0) + 1
          loadGenerationRef.current[id] = requestId
          const expectedCursor = pager.startOlderPage(requestId)
          if (!expectedCursor || !pager.current().hasEarlier) return
          transcriptAccumulator(id).startSnapshot(requestId)
          try {
            const data = await api.get<unknown>(
              `/api/apps/nova/discussions/${encodeURIComponent(id)}/history-page?limit=${HISTORY_PAGE_LIMIT}&before=${encodeURIComponent(expectedCursor)}`,
            )
            if (!isHistoryPageResponse(data)) return
            if (historySessionIdsRef.current.has(id)
              && historySessionIdsRef.current.get(id) !== historyResponseSessionId(data)) {
              resetHistoryPaging(id)
              loadedRef.current.delete(id)
              void loadMessages(id, INITIAL_HISTORY_TAIL, true)
              return
            }
            commitHistoryPage(id, requestId, data, "before", expectedCursor)
          } catch (error) {
            if (error instanceof ApiError && error.status === 409
              && (error.code === "history_cursor_stale" || error.code === "history_cursor_mismatch")) {
              resetHistoryPaging(id)
              loadedRef.current.delete(id)
              // Queue a newest-page recovery behind this anchored request.
              void loadMessages(id, INITIAL_HISTORY_TAIL, true)
            }
          }
        })
        return
      }

      const currentTail = legacyHistoryTailRef.current[id] ?? INITIAL_HISTORY_TAIL
      if (currentTail >= MAX_HISTORY_TAIL) {
        setHasEarlierMessages((prev) => ({ ...prev, [id]: false }))
        return
      }
      const nextTail = Math.min(MAX_HISTORY_TAIL, currentTail + HISTORY_TAIL_STEP)
      await loadMessages(id, nextTail, true)
    } finally {
      loadingEarlierRef.current.delete(id)
      setLoadingEarlierDiscussionId((current) => current === id ? null : current)
    }
  }, [commitHistoryPage, durableTranscriptPager, hasEarlierMessages, loadMessages, resetHistoryPaging, transcriptAccumulator])

  const reloadActiveMessages = useCallback((force?: boolean) => {
    const id = activeIdRef.current
    if (!id) return
    if (force) {
      setStreaming((prev) => ({ ...prev, [id]: false }))
    } else if (streamingRef.current[id]) {
      return
    }
    loadedRef.current.delete(id)
    if (force && historyModesRef.current.get(id) === "v2")
      void catchUpMessages(id)
    else
      void loadMessages(id)
  }, [catchUpMessages, loadMessages])

  const selectDiscussion = useCallback((id: string) => {
    setActiveDiscussionId(id)
    startTransition(() => {
      // Render an already loaded discussion immediately, then revalidate its
      // current tail. WebSocket delivery is best-effort across mobile suspend
      // and network handoffs; revisiting a cached discussion is the durable
      // recovery boundary for any messages or tool calls missed in between.
      const wasLoaded = loadedRef.current.has(id)
      const tail = legacyHistoryTailRef.current[id] ?? INITIAL_HISTORY_TAIL
      const revision = discussionsRef.current.find((discussion) => discussion.id === id)
        ?.conversationRevision ?? 0
      const pager = durableTranscriptPagersRef.current.get(id)
      const refresh = shouldCatchUpHistory(
        historyModesRef.current.get(id),
        pager?.current().newestCursor,
        wasLoaded,
      )
        ? catchUpMessages(id)
        : loadMessages(id, tail, false)
      void refresh
        .then(() => acknowledgeRead(id, revision))
    })
  }, [acknowledgeRead, catchUpMessages, loadMessages])

  const clearDiscussionSelection = useCallback(() => {
    setActiveDiscussionId(null)
  }, [])

  const visibleDiscussions = useMemo(
    () => discussions.filter((d) => !dismissedIds.has(d.id) && !isClosed(d.status)),
    [discussions, dismissedIds],
  )

  useEffect(() => {
    if (activeDiscussionId && !loadedRef.current.has(activeDiscussionId)) {
      loadMessages(activeDiscussionId)
    }
  }, [discussions, activeDiscussionId, loadMessages])

  const createDiscussion = useCallback(async (agentId?: string, qualityTier?: string, provider?: string) => {
    setIsSpawning(true)
    try {
      const body: Record<string, string> = {}
      if (agentId) body.agentId = agentId
      if (qualityTier) body.qualityTier = qualityTier
      if (provider) body.provider = provider
      const d = await api.post<DiscussionInfo>("/api/apps/nova/discussions", Object.keys(body).length ? body : undefined)
      historyLifecycleRef.current.revive(d.id)
      // The creation websocket can beat the HTTP response and insert a sparse
      // placeholder. Always replace it with the authoritative returned record
      // so every surface gets the entity and session ids needed for chat.
      upsertDiscussion(d)
      setActiveDiscussionId(d.id)
      setMessages((prev) => ({ ...prev, [d.id]: [] }))
      loadedRef.current.add(d.id)
      return d
    } finally {
      setIsSpawning(false)
    }
  }, [])

  const deliverMessage = useCallback(async (
    discussionId: string,
    content: string,
    images?: ImageAttachment[],
    options?: SendOptions,
    input?: ChatInputPart[],
    attachments?: UploadedAttachment[],
  ) => {
    const disc = discussions.find((d) => d.id === discussionId)
    if (!disc) return

    // The shared queue has already rendered the outgoing bubble. For an idle
    // send, show the running state in the same render cycle instead of waiting
    // for HTTP admission and a later provider lifecycle event.
    const locallyStartedTurn = !!options?.idempotencyKey && !streamingRef.current[discussionId]
    if (locallyStartedTurn) {
      lastSendAtRef.current[discussionId] = Date.now()
      activeObservedAfterSendRef.current[discussionId] = false
      latchStreaming(discussionId)
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: "thinking" as const } : d)
      )
    }

    const displayContent = options?.displayContent ?? (
      content
        .replace(/<nova-context[\s\S]*?<\/nova-context>\s*/g, "")
        .replace(/<nova-prior-messages?[\s\S]*?<\/nova-prior-messages?>\s*/g, "")
        .trim()
      || attachments?.map(attachment => attachment.name).join(", ")
      || (images?.length ? "Image" : "New discussion")
    )

    if (!disc.title && disc.messageCount === 0) {
      const title = displayContent.length > 60 ? displayContent.slice(0, 59) + "…" : displayContent
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, title, titleSource: "fallback" } : d)
      )
      api.put<DiscussionInfo>(
        `/api/apps/nova/discussions/${discussionId}/title/fallback`,
        { title },
      ).then(upsertDiscussion).catch(() => {})
    }

    type Admission = {
      success: boolean
      accepted: boolean
      sessionId?: string
      disposition: "queued" | "delivered"
      queueItemId?: string | null
      metadata?: Record<string, unknown>
      messageUid?: string | null
    }

    const body = input
      ? { input, inputMethod: options?.inputMethod, delivery: options?.delivery, displayContent }
      : { content, images, inputMethod: options?.inputMethod, delivery: options?.delivery, displayContent }
    let res: Admission
    try {
      res = options?.idempotencyKey
        ? await api.postWithHeaders<Admission>(
            `/api/apps/nova/discussions/${discussionId}/message`, body,
            { "X-Idempotency-Key": options.idempotencyKey },
          )
        : await api.post<Admission>(`/api/apps/nova/discussions/${discussionId}/message`, body)
    } catch (error) {
      if (locallyStartedTurn) clearStreamingLatch(discussionId)
      throw error
    }

    if (res.sessionId && res.sessionId !== disc.sessionId) {
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, sessionId: res.sessionId! } : d)
      )
    }

    if (res.disposition === "delivered") {
      // The shared remote queue owns the immediately visible outgoing bubble.
      // Direct callers without its idempotency identity retain the legacy append.
      if (!options?.idempotencyKey) {
        const userMsg: MessageBlock = {
          id: res.messageUid ?? crypto.randomUUID(),
          role: "user",
          parts: [{ type: "text", content: displayContent, images, attachments }],
          timestamp: new Date().toISOString(),
          ...(res.metadata ? { metadata: res.metadata } : {}),
        }
        setMessages((prev) => {
          const current = prev[discussionId] ?? []
          return current.some(message => message.id === userMsg.id)
            ? prev
            : { ...prev, [discussionId]: [...current, userMsg] }
        })
      }
      lastSendAtRef.current[discussionId] = Date.now()
      activeObservedAfterSendRef.current[discussionId] = false
      latchStreaming(discussionId)
      setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
      setResumePending((prev) => ({ ...prev, [discussionId]: false }))
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: "thinking" as const } : d)
      )
    }
    return res
  }, [discussions, clearStreamingLatch, latchStreaming])

  const sendMessage = useCallback((discussionId: string, content: string, images?: ImageAttachment[], options?: SendOptions) =>
    deliverMessage(discussionId, content, images, options), [deliverMessage])

  const sendInput = useCallback((discussionId: string, input: ChatInputPart[], attachments: UploadedAttachment[], options?: SendOptions) => {
    const content = input
      .filter((part): part is Extract<ChatInputPart, { type: "text" }> => part.type === "text")
      .map(part => part.text)
      .join("\n")
    return deliverMessage(discussionId, content, undefined, options, input, attachments)
  }, [deliverMessage])

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
    activeObservedAfterSendRef.current[discussionId] = true
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
    if (event.type === "ai-session.changed") {
      const { sessionId } = event.data as { sessionId?: string }
      if (!sessionId) return
      const discId = sessionToDiscussion.get(sessionId)
      if (!discId) return

      // Confidential stream and lifecycle frames are deliberately replaced by
      // this opaque invalidation. Recover through the authorized transcript API;
      // never reconstruct content from the ambient WebSocket payload.
      loadedRef.current.delete(discId)
      if (activeIdRef.current !== discId) return
      confidentialInvalidations.schedule(discId, () => {
        const current = discussionsRef.current.find((discussion) => discussion.id === discId)
        if (!current || current.sessionId !== sessionId || isClosed(current.status)) return
        if (activeIdRef.current !== discId) return
        loadedRef.current.delete(discId)
        void catchUpMessages(discId, sessionId)
        void reconcileStreaming()
      })
    } else if (event.type === "session.input-queue.updated") {
      const update = event.data as { sessionId?: string; transition?: string }
      if (!update.sessionId) return
      const discId = sessionToDiscussion.get(update.sessionId)
      if (!discId) return
      environment.window.dispatchEvent(new CustomEvent("nova:input-queue-updated", {
        detail: { discussionId: discId, sessionId: update.sessionId, transition: update.transition },
      }))
      if (update.transition === "delivered") {
        lastSendAtRef.current[discId] = Date.now()
        activeObservedAfterSendRef.current[discId] = false
        latchStreaming(discId)
        setDiscussions((prev) => applySessionStatus(prev, discId, "Active"))
        loadedRef.current.delete(discId)
        void catchUpMessages(discId, update.sessionId, true)
        void refreshDiscussions()
      }
    } else if (event.type === "session.updated") {
      const session = event.data as { id: string; status: string; stopReason?: string; title?: string }
      const discId = sessionToDiscussion.get(session.id)
      if (!discId) return
      const known = discussionsRef.current.find((d) => d.id === discId)
      if (shouldRequestSessionTitleSync(known, session.title)) {
        void api.put<DiscussionInfo>(`/api/apps/nova/discussions/${discId}/title/session`)
          .then(upsertDiscussion)
          .catch(() => {})
      }

      const updateGeneration = (sessionUpdateGenerationRef.current[discId] ?? 0) + 1
      sessionUpdateGenerationRef.current[discId] = updateGeneration
      if (session.status !== "Active") {
        const nowMs = Date.now()
        if (preservesRecentStreamingLatch(
          session.status,
          !!streamingRef.current[discId],
          lastSendAtRef.current[discId] ?? 0,
          nowMs,
          SEND_GRACE_MS,
          !!activeObservedAfterSendRef.current[discId],
        )) {
          environment.window.setTimeout(
            () => {
              // Re-run the one settlement path after the pre-Active grace.
              // Any newer lifecycle event cancels this stale callback.
              if (sessionUpdateGenerationRef.current[discId] === updateGeneration)
                handleWsEventRef.current?.(event)
            },
            Math.max(0, SEND_GRACE_MS - (nowMs - (lastSendAtRef.current[discId] ?? 0))) + 50,
          )
          return
        }
        clearStreamingLatch(discId)
        delete activeObservedAfterSendRef.current[discId]
        // Closed (archived/archiving) discussions are terminal: never echo
        // session events back to the server for them.
        if (!known || isClosed(known.status) || isDiscussionArchivePending(discId)) return
        const isRestartRecovery = session.status === "Stopped"
          && (session.stopReason === "maintenance_restart" || session.stopReason === "orphaned_on_restart")
        const isStopped = !isRestartRecovery
          && (session.status === "Stopped" || session.status === "Error")
        setDiscussions((prev) =>
          applySettledSessionStatus(prev, discId, session.status, new Date().toISOString(), session.stopReason)
        )
        if (isStopped) {
          api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
        } else {
          const scheduleTranscriptReload = (conversationRevision?: number) => {
            environment.window.setTimeout(async () => {
              if (sessionUpdateGenerationRef.current[discId] !== updateGeneration) return
              // Inactive discussions recover on their next selection.
              loadedRef.current.delete(discId)
              if (activeIdRef.current !== discId) return
              await catchUpMessages(discId)
              if (conversationRevision !== undefined)
                await acknowledgeRead(discId, conversationRevision)
            }, SETTLED_TRANSCRIPT_RELOAD_DELAY_MS)
          }
          void api.put<DiscussionInfo>(`/api/apps/nova/discussions/${discId}/activity`)
            .then((updated) => {
              upsertDiscussion(updated)
              scheduleTranscriptReload(updated.conversationRevision)
            })
            .catch(() => scheduleTranscriptReload())
        }
      } else {
        // The discussion list and the transcript must describe the same runtime.
        // A turn can start in another window or through an automation, so the
        // local optimistic send is not an authoritative source of this state.
        activeObservedAfterSendRef.current[discId] = true
        setDiscussions((prev) => applySessionStatus(prev, discId, session.status))
        // Active means a turn is running even when this client didn't start
        // it — an automation, the heartbeat, or the same discussion open on
        // his phone. Without this there is no path back to streaming=true, so
        // the composer looks idle and the message queue drains straight into a
        // live turn. A pending question is the exception: the turn is Active
        // but blocked on the user, and answers go through onAnswerQuestion.
        if (!pendingQuestionsRef.current[discId]) latchStreaming(discId)
      }
    } else if (event.type === "session.ended") {
      const { id, stopReason } = event.data as { id: string; stopReason?: string }
      const discId = sessionToDiscussion.get(id)
      if (!discId) return
      sessionUpdateGenerationRef.current[discId] =
        (sessionUpdateGenerationRef.current[discId] ?? 0) + 1
      delete activeObservedAfterSendRef.current[discId]
      setStreaming((prev) => ({ ...prev, [discId]: false }))
      // A card still up when the session died was never answered.
      clearQuestion(discId, pendingQuestionsRef.current[discId] ? "session_ended" : questionStatesRef.current[discId]?.outcome ?? null)
      setInterrupting((prev) => ({ ...prev, [discId]: false }))
      setResumePending((prev) => ({ ...prev, [discId]: false }))
      const known = discussionsRef.current.find((d) => d.id === discId)
      const closed = !known || isClosed(known.status) || isDiscussionArchivePending(discId)
      const isRestartRecovery = stopReason === "maintenance_restart" || stopReason === "orphaned_on_restart"
      setDiscussions((prev) =>
        prev.map((d) => d.id === discId && !isClosed(d.status)
          ? { ...d, status: isRestartRecovery ? "idle" as const : "stopped" as const }
          : d)
      )
      if (!closed && !isRestartRecovery)
        api.put(`/api/apps/nova/discussions/${discId}/stopped`).catch(() => {})
    } else if (event.type === "discussion.created") {
      const { discussionId, agentId, status, type } = event.data as { discussionId: string; agentId?: string; status?: string; type?: string }
      if (!discussionId) return
      historyLifecycleRef.current.revive(discussionId)
      setDiscussions((prev) => {
        if (prev.some((d) => d.id === discussionId)) return prev
        const newDisc: DiscussionInfo = {
          id: discussionId,
          // The WS event doesn't carry the entity id; share stays disabled for
          // this placeholder until the next discussions refresh fills it in.
          entityId: "",
          title: null,
          titleSource: null,
          sessionId: null,
          status: (status ?? "idle") as DiscussionInfo["status"],
          type: (type ?? "chat") as DiscussionInfo["type"],
          createdAt: new Date().toISOString(),
          lastActivity: new Date().toISOString(),
          messageCount: 0,
          lastReadAt: null,
          conversationRevision: 0,
          readConversationRevision: 0,
          agentId: agentId ?? null,
        }
        return [newDisc, ...prev]
      })
      // Clients that did not originate the create have only the sparse event
      // payload. Hydrate the canonical record so title/session routing works.
      void refreshDiscussions()
    } else if (event.type === "discussion.changed") {
      const { discussionId } = event.data as { discussionId?: string }
      if (!discussionId) return
      void refreshDiscussions()
      if (activeIdRef.current !== discussionId) return
      loadedRef.current.delete(discussionId)
      void catchUpMessages(discussionId)
    } else if (event.type === "discussion.event") {
      const { discussionId, content, source, senderAgentId, messageUid, metadata, timestamp: serverTs } = event.data as { discussionId: string; sessionId: string; content: string; source: string; senderAgentId?: string; messageUid?: string | null; metadata?: Record<string, unknown> | null; timestamp?: string }
      if (!discussionId) return
      const ts = serverTs ?? new Date().toISOString()
      if (messageUid && shouldAccumulatePushedHistoryOverlay(
        historyModesRef.current.get(discussionId),
      )) {
        const sourceTag = source ? `event:${source}` : "event:system"
        const overlay: DiscussionHistoryOverlay = {
          id: messageUid,
          messageUid,
          role: "assistant",
          parts: [
            { type: "text", content },
            { type: "event_data", content: JSON.stringify(metadata ?? {}) },
          ],
          timestamp: ts,
          senderAgentId,
          source: sourceTag,
        }
        const overlays = accumulateHistoryOverlays(
          historyOverlaysRef.current.get(discussionId) ?? new Map(),
          [overlay],
        )
        historyOverlaysRef.current.set(discussionId, overlays)
      }
      setDiscussions((prev) => applyDiscussionMessageArrival(prev, discussionId, ts))
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
      const { discussionId, content, audioUrl, senderAgentId, messageUid, timestamp: serverTs,
        conversationRevision, readConversationRevision } = event.data as {
          discussionId: string
          content: string
          audioUrl?: string
          senderAgentId?: string
          messageUid?: string
          timestamp?: string
          conversationRevision?: number
          readConversationRevision?: number
        }
      if (!discussionId) return
      const ts = serverTs ?? new Date().toISOString()
      if (messageUid && shouldAccumulatePushedHistoryOverlay(
        historyModesRef.current.get(discussionId),
      )) {
        const parts: DiscussionHistoryOverlay["parts"] = [{ type: "text", content }]
        if (audioUrl) parts.push({ type: "audio", content: audioUrl })
        const overlay: DiscussionHistoryOverlay = {
          id: messageUid,
          messageUid,
          role: "assistant",
          parts,
          timestamp: ts,
          senderAgentId,
          source: "nova-message",
        }
        const overlays = accumulateHistoryOverlays(
          historyOverlaysRef.current.get(discussionId) ?? new Map(),
          [overlay],
        )
        historyOverlaysRef.current.set(discussionId, overlays)
      }
      const isViewing = activeIdRef.current === discussionId
      setDiscussions((prev) => typeof conversationRevision === "number"
        ? applyConversationMessageArrival(
            prev,
            discussionId,
            ts,
            conversationRevision,
            readConversationRevision,
          )
        : applyDiscussionMessageArrival(prev, discussionId, ts))
      setMessages((prev) => {
        const current = prev[discussionId] ?? []
        const merged = mergeNovaMessageArrival(current, {
          content,
          audioUrl,
          senderAgentId,
          messageUid,
          timestamp: ts,
          fallbackId: `nova-msg-${Date.now()}`,
        })
        return merged === current ? prev : { ...prev, [discussionId]: merged }
      })
      if (isViewing && typeof conversationRevision === "number")
        void acknowledgeRead(discussionId, conversationRevision)
    } else if (event.type === "discussion.user-message") {
      const { discussionId, sessionId, messageUid } = event.data as { discussionId?: string; sessionId?: string | null; messageUid?: string | null }
      if (!discussionId) return
      if (sessionId) {
        setDiscussions((prev) => prev.map((discussion) => discussion.id === discussionId
          ? { ...discussion, sessionId }
          : discussion))
      }
      // The send is now durable in RedCompute. Revalidate every mounted runtime
      // viewing it instead of copying the queue winner's optimistic block across
      // views; this also converges other open clients and attachment-rich messages.
      loadedRef.current.delete(discussionId)
      if (activeIdRef.current !== discussionId) return
      if (historyModesRef.current.get(discussionId) !== "v2"
        && messageUid
        && (messagesRef.current[discussionId] ?? []).some(message => message.id === messageUid)) return
      void catchUpMessages(discussionId, sessionId, true)
    } else if (event.type === "discussion.cleared") {
      const { discussionId } = event.data as { discussionId: string }
      if (!discussionId) return
      setDiscussions((prev) => prev.map((discussion) => discussion.id === discussionId
        ? { ...discussion, conversationRevision: 0, readConversationRevision: 0 }
        : discussion))
      setMessages((prev) => ({ ...prev, [discussionId]: [] }))
      resetHistoryPaging(discussionId)
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
      clearQuestion(discussionId, null)
      setInterrupting((prev) => ({ ...prev, [discussionId]: false }))
      setResumePending((prev) => ({ ...prev, [discussionId]: false }))
      loadedRef.current.delete(discussionId)
      loadMessages(discussionId, INITIAL_HISTORY_TAIL, true)
    } else if (event.type === "discussion.rotated") {
      const { oldDiscussionId, newDiscussionId } = event.data as { oldDiscussionId: string; newDiscussionId: string; agentId: string }
      historyLifecycleRef.current.revive(newDiscussionId)
      setDiscussions((prev) => prev.filter((d) => d.id !== oldDiscussionId))
      setMessages((prev) => { const next = { ...prev }; delete next[oldDiscussionId]; return next })
      retireHistoryPaging(oldDiscussionId)
      setActiveDiscussionId((current) => resolveRotatedDiscussionSelection(current, oldDiscussionId, newDiscussionId))
      refreshDiscussions()
    } else if (event.type === "session.stream") {
      const { sessionId, event: evt, timestamp: serverTimestamp } = event.data as {
        sessionId: string
        event: ClaudeStreamEvent
        timestamp?: string
      }
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
        payloadRef: evt.payloadRef ?? null,
        messageId: evt.messageId ?? null,
        messageUid: evt.messageUid ?? null,
        phase: evt.phase ?? null,
        timestamp: serverTimestamp ?? new Date().toISOString(),
        requestId: evt.requestId ?? null,
        epoch: evt.epoch ?? null,
        sequence: evt.sequence ?? null,
      }

      setMessages((prev) => {
        const current = prev[discId] ?? []
        const result = processStreamEvent(current, true, chatEvent, resumePendingRef.current[discId] ?? false, questionStatesRef.current[discId])
        const reconciled = transcriptAccumulator(discId).receiveLive(chatEvent, current)
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
        } else if (result.isStreaming || result.resumePending) {
          // Recover when session.updated raced ahead of a newly-associated
          // session id. Any live stream activity is sufficient evidence that
          // the discussion is running.
          setDiscussions((p) => applySessionStatus(p, discId, "Active"))
        }
        if (reconciled.gapDetected) {
          queueMicrotask(() => void catchUpMessages(discId, sessionId))
        }
        return { ...prev, [discId]: reconciled.messages }
      })
    }
  }, [sessionToDiscussion, clearQuestion, clearStreamingLatch, refreshDiscussions, loadMessages, catchUpMessages, environment.window, latchStreaming, acknowledgeRead, transcriptAccumulator, confidentialInvalidations, reconcileStreaming, resetHistoryPaging, retireHistoryPaging])
  handleWsEventRef.current = handleWsEvent

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
    // The socket cannot replay frames emitted while the client was suspended.
    // Refresh the active tail now and make every other cached discussion load
    // authoritatively the next time it is selected.
    loadedRef.current.clear()
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
    markDiscussionArchivePending(id)
    if (activeIdRef.current === id) setActiveDiscussionId(null)
    try {
      await api.delete(`/api/apps/nova/discussions/${id}`)
      setMessages((prev) => { const next = { ...prev }; delete next[id]; return next })
      retireHistoryPaging(id)
      // Intent stays in the set after success: the server now owns the state
      // and default list fetches exclude closed discussions anyway.
    } catch (err) {
      clearDiscussionArchivePending(id)
      if (disc) setDiscussions((ds) => ds.map((d) => d.id === id ? { ...d, status: disc.status } : d))
      toast({ variant: "error", title: "Failed to archive", description: err instanceof Error ? err.message : "Unknown error" })
    }
  }, [retireHistoryPaging, toast])

  const dismissDiscussion = useCallback((id: string) => {
    setDismissedIds((prev) => new Set(prev).add(id))
    setDiscussions((prev) => prev.filter((d) => d.id !== id))
    setMessages((prev) => { const next = { ...prev }; delete next[id]; return next })
    retireHistoryPaging(id)
    if (activeDiscussionId === id) setActiveDiscussionId(null)
  }, [activeDiscussionId, retireHistoryPaging])

  const rotateDiscussion = useCallback(async (id: string): Promise<string | null> => {
    try {
      const disc = discussions.find((d) => d.id === id)
      let newDiscussionId: string | null = null
      if (disc?.type === "heartbeat" && disc.agentId) {
        const response = await api.post<{ discussionId: string | null }>(`/api/apps/nova/heartbeat/${disc.agentId}/rotate`)
        newDiscussionId = response.discussionId
      } else {
        const response = await api.post<{ archived: DiscussionInfo; created: DiscussionInfo }>(`/api/apps/nova/discussions/${id}/rotate`)
        newDiscussionId = response.created.id
        upsertDiscussion(response.created)
      }
      setDiscussions((prev) => prev.filter((d) => d.id !== id))
      setMessages((prev) => { const next = { ...prev }; delete next[id]; return next })
      retireHistoryPaging(id)
      if (newDiscussionId) {
        const replacementId = newDiscussionId
        historyLifecycleRef.current.revive(replacementId)
        setActiveDiscussionId((current) => resolveRotatedDiscussionSelection(current, id, replacementId))
      }
      const label = disc?.type === "heartbeat" ? "Heartbeat" : "LIVE"
      toast({ variant: "success", title: `${label} rotated`, description: `Fresh ${label} discussion created` })
      return newDiscussionId
    } catch (err) {
      toast({ variant: "error", title: "Failed to rotate", description: err instanceof Error ? err.message : "Unknown error" })
      return null
    }
  }, [retireHistoryPaging, toast, discussions])

  const renameDiscussion = useCallback(async (id: string, title: string) => {
    const updated = await api.put<DiscussionInfo>(`/api/apps/nova/discussions/${id}/title`, { title })
    upsertDiscussion(updated)
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
    sendInput,
    interruptDiscussion,
    answerQuestion,
    archiveDiscussion,
    rotateDiscussion,
    dismissDiscussion,
    renameDiscussion,
    setConfidential,
    resumeDiscussion,
    loadEarlierMessages,
    refreshDiscussions,
    syncAndRefresh,
    reloadActiveMessages,
    handleWsEvent,
    isLoadingMessages: loadingDiscussionId === activeDiscussionId && loadingDiscussionId !== null,
    hasEarlierMessages: activeDiscussionId ? hasEarlierMessages[activeDiscussionId] ?? false : false,
    isLoadingEarlier: loadingEarlierDiscussionId === activeDiscussionId && loadingEarlierDiscussionId !== null,
    upstreamConnected,
    handleUpstreamDisconnect,
    handleUpstreamReconnect,
  }
}
