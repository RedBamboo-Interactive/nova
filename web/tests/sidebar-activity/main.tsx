import React from "react"
import { createRoot } from "react-dom/client"
import { MemoryRouter } from "react-router-dom"
import type { BackgroundTaskInfo } from "@redbamboo/chat"
import { DiscussionSidebar } from "../../src/components/discussion/discussion-sidebar"
import type { AgentInfo, DiscussionDelegationActivity, DiscussionInfo } from "../../src/lib/types"
import "./fixture.css"

// One sidebar indicator per discussion, chosen by the chat activity row's
// priority: the session working, then agents, then other background tasks,
// then delegations, then the idle or stopped state.

const now = new Date().toISOString()
const discussion = (id: string, title: string, status: DiscussionInfo["status"]): DiscussionInfo => ({
  id, entityId: `entity-${id}`, title, titleSource: "system", sessionId: `session-${id}`, status, type: "chat",
  createdAt: now, lastActivity: now, messageCount: 4, lastReadAt: now, conversationRevision: 1, readConversationRevision: 1, agentId: "nova",
} as DiscussionInfo)
const discussions = [
  discussion("session", "Session thinking while an agent runs", "thinking"),
  discussion("agents", "Agents, a shell and a delegation", "idle"),
  discussion("tasks", "A background shell and a delegation", "idle"),
  discussion("delegations", "A delegation waiting to resume", "idle"),
  discussion("idle", "Nothing running", "idle"),
]
const delegation = (status: "running" | "waiting_to_resume"): DiscussionDelegationActivity => ({
  ongoingCount: 1, available: true, unknownCount: 0, linkedSessionIds: ["code-1"],
  sessions: [{ sessionId: "code-1", title: "Sentinel shield", repositoryId: null, repository: "RedLeaf", status, available: true }],
})
const agent = (taskId: string): BackgroundTaskInfo => ({ taskId, kind: "agent", status: "running", description: "Check Sentinel 5xx handling", startedAt: now })
const shell: BackgroundTaskInfo = { taskId: "shell", kind: "shell", status: "running", description: "npm test", startedAt: now }
const avatar = "data:image/svg+xml," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><defs><linearGradient id="g" x2="1" y2="1"><stop stop-color="#f5d07a"/><stop offset="1" stop-color="#4b7a4a"/></linearGradient></defs><rect width="10" height="10" fill="url(#g)"/></svg>')
const nova: AgentInfo = { id: "nova", name: "Nova", avatarUrl: avatar } as AgentInfo
const noop = () => {}

createRoot(document.getElementById("root")!).render(
  <MemoryRouter>
    <div style={{ width: 420, height: "100vh" }} className="bg-bg-surface">
      <DiscussionSidebar discussions={discussions} loadState="ready" onRetryLoad={noop}
        delegationActivity={{ session: delegation("running"), agents: delegation("running"), tasks: delegation("running"), delegations: delegation("waiting_to_resume") }}
        backgroundTasks={{ session: [agent("a0")], agents: [agent("a1"), agent("a2"), shell], tasks: [shell] }}
        activeDiscussionId={null} onSelect={noop} onArchive={noop} onRotate={noop} onDismiss={noop} onRowAction={noop} onUpdateTitle={noop}
        getAgent={() => nova} multiAgent />
    </div>
  </MemoryRouter>,
)
