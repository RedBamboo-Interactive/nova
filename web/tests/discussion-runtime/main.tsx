import React from "react"
import { createRoot } from "react-dom/client"
import { ToastProvider } from "@redbamboo/ui"
import { api } from "../../src/lib/api"
import { useDiscussions } from "../../src/hooks/use-discussions"
import { setDiscussionList } from "../../src/lib/discussion-list-store"

const discussion = { id: "live", entityId: "entity", title: "LIVE fixture", titleSource: "system", sessionId: "session-live", status: "idle", type: "live", createdAt: "2026-10-02T12:00:00Z", lastActivity: "2026-10-02T12:00:00Z", messageCount: 0, lastReadAt: null, conversationRevision: 0, readConversationRevision: 0, agentId: "agent" } as const
const test = { status: "Active", hold: false, pending: [] as (() => void)[], runtime: null as ReturnType<typeof useDiscussions> | null,
  listStatus: "idle" as "idle" | "archived",
  admission: { accepted: true, disposition: "queued" } as Record<string, unknown>,
  holdAdmission: false, pendingAdmission: [] as (() => void)[],
  holdAuthority: false, pendingAuthority: [] as (() => void)[], authorityReads: 0, unavailableAuthority: false,
  admit: async () => {
    const admission = test.admission
    if (test.holdAdmission) await new Promise<void>(resolve => test.pendingAdmission.push(resolve))
    return admission
  },
  replace: (sessionId: string, status: "idle" | "archived" = "idle") => setDiscussionList([{ ...discussion, sessionId, status }]),
  legacy: new URLSearchParams(location.search).has("legacy"),
  event: (status: string) => test.runtime!.handleWsEvent({ type: "session.updated", data: { id: "session-live", status } }),
  stream: (event: object) => test.runtime!.handleWsEvent({ type: "session.stream", data: { sessionId: "session-live", event } }),
}
Object.assign(api, {
  get: async (path: string) => {
    if (path.includes("delegations")) return {}
    if (path === "/api/apps/nova/discussions") return [{ ...discussion, status: test.listStatus }]
    if (path.includes("/ai-session/sessions?")) return [{ id: "session-live", status: test.status }]
    if (path.includes("history-page")) {
      const status = test.status
      if (test.hold) await new Promise<void>(resolve => test.pending.push(resolve))
      if (test.legacy) return { discussion, messages: [] }
      return { discussion, session: { id: "session-live", status }, messages: [], overlays: [], page: { direction: "newest", epoch: null, oldestCursor: null, newestCursor: null, hasEarlier: false, hasLater: false, fromSequence: null, throughSequence: null, boundaryComplete: true } }
    }
    if (path.startsWith("/ai-session/sessions/")) {
      const status = test.status
      test.authorityReads++
      if (test.holdAuthority) await new Promise<void>(resolve => test.pendingAuthority.push(resolve))
      if (test.unavailableAuthority) throw new Error("Fixture: lifecycle unavailable")
      if (test.hold) await new Promise<void>(resolve => test.pending.push(resolve))
      return { session: { id: "session-live", status }, messages: [] }
    }
    return { discussion, messages: [] }
  },
  post: () => test.admit(),
  postWithHeaders: () => test.admit(),
  put: async () => ({ ...discussion }),
})
setDiscussionList([{ ...discussion }])
function App() {
  const runtime = useDiscussions()
  test.runtime = runtime
  return <><button onClick={() => runtime.selectDiscussion("live")}>Open LIVE</button><output data-testid="state">{JSON.stringify({ streaming: runtime.isStreaming, resumePending: runtime.isResumePending, question: !!runtime.pendingQuestion, status: runtime.activeDiscussion?.status, sessionId: runtime.activeDiscussion?.sessionId })}</output></>
}
;(window as unknown as { runtimeTest: typeof test }).runtimeTest = test
createRoot(document.getElementById("root")!).render(<ToastProvider><App /></ToastProvider>)
