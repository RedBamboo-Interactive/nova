import { useCallback } from "react"
import { createRoot } from "react-dom/client"
import { ToastProvider } from "@redbamboo/ui"
import { ChatPanel, type PermissionDecisionPayload, type QuestionAnswerPayload } from "@redbamboo/chat"
import { api } from "../../src/lib/api"
import { useDiscussions } from "../../src/hooks/use-discussions"
import { setDiscussionList } from "../../src/lib/discussion-list-store"
import { NovaStatusLine } from "../../src/components/nova-status-line"
import "./fixture.css"

// Nova's real discussion runtime (its session.stream mapping, its answer and
// decision posts, its reload hydration) feeding the shared ChatPanel with the
// question and approval props ChatView passes. The page decides what a person
// on a phone is offered when a session parks on them.
const discussion = { id: "live", entityId: "entity", title: "LIVE fixture", titleSource: "system", sessionId: "session-live", status: "idle", type: "live", createdAt: "2026-11-09T12:00:00Z", lastActivity: "2026-11-09T12:00:00Z", messageCount: 0, lastReadAt: null, conversationRevision: 0, readConversationRevision: 0, agentId: "agent" } as const
const page = { direction: "newest", epoch: null, oldestCursor: null, newestCursor: null, hasEarlier: false, hasLater: false, fromSequence: null, throughSequence: null, boundaryComplete: true }

const test = {
  status: "Active",
  /** The session read's pendingQuestions (RedCompute's live control state). */
  snapshot: [] as unknown[],
  posts: [] as { path: string; body: unknown }[],
  runtime: null as ReturnType<typeof useDiscussions> | null,
  stream: (event: object) => test.runtime!.handleWsEvent({ type: "session.stream", data: { sessionId: "session-live", event } }),
}
Object.assign(api, {
  get: async (path: string) => {
    if (path.includes("delegations")) return {}
    if (path === "/api/apps/nova/discussions") return [{ ...discussion }]
    if (path.includes("/ai-session/sessions?")) return [{ id: "session-live", status: test.status }]
    if (path.includes("history-page"))
      return { discussion, session: { id: "session-live", status: test.status }, messages: [], overlays: [], page }
    if (path.startsWith("/ai-session/sessions/"))
      return { session: { id: "session-live", status: test.status }, messages: [], pendingQuestions: test.snapshot }
    return { discussion, messages: [] }
  },
  post: async (path: string, body: unknown) => {
    test.posts.push({ path, body })
    return path.endsWith("/question") ? { ok: true } : { accepted: true, disposition: "queued" }
  },
  postWithHeaders: async () => ({ accepted: true, disposition: "queued" }),
  put: async () => ({ ...discussion }),
})
setDiscussionList([{ ...discussion }])

function App() {
  const runtime = useDiscussions()
  test.runtime = runtime
  const id = runtime.activeDiscussionId
  // As ChatView: answers and decisions go to the open discussion.
  const onAnswerQuestion = useCallback((answer: string, payload?: QuestionAnswerPayload) => {
    if (id) void runtime.answerQuestion(id, answer, payload)
  }, [id, runtime.answerQuestion])
  const onPermissionDecision = useCallback((payload: PermissionDecisionPayload) => (
    id ? runtime.decidePermission(id, payload) : Promise.reject(new Error("No discussion is open."))
  ), [id, runtime.decidePermission])
  if (!runtime.activeDiscussion) return <button onClick={() => runtime.selectDiscussion("live")}>Open LIVE</button>
  return (
    <div style={{ width: "100%", height: "100vh", display: "flex", flexDirection: "column" }}>
      <ChatPanel
        messages={runtime.activeMessages}
        isStreaming={runtime.isStreaming}
        sessionId="live"
        persistQueue={false}
        hasServerSession
        pendingQuestion={runtime.pendingQuestion}
        questionOutcome={runtime.questionOutcome}
        onAnswerQuestion={onAnswerQuestion}
        permissionApprovals={runtime.permissionApprovals}
        onPermissionDecision={onPermissionDecision}
        renderStatusLine={({ isStreaming, isReconnecting, messages }) => (
          <NovaStatusLine isStreaming={isStreaming} isReconnecting={isReconnecting} messages={messages} />
        )}
      />
    </div>
  )
}
;(window as unknown as { approvalTest: typeof test }).approvalTest = test
createRoot(document.getElementById("root")!).render(<ToastProvider><App /></ToastProvider>)
