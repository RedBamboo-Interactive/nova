import { test } from "node:test"
import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import type { QuestionState } from "@redbamboo/chat"
import { processStreamEvent } from "../../../../redbamboo-packages/packages/chat/src/lib/process-stream-event.ts"
import { abandonPermissions, closePermission, permissionPendingQuestion, readPermissionRequest, visibleApprovals } from "../../../../redbamboo-packages/packages/chat/src/lib/permission-approval.ts"
import { historyRevalidationDirection, shouldCatchUpHistory } from "./discussion-history-page.ts"
import { mayHoldPendingApproval, restoreApprovals, type ApprovalLifecycle } from "./permission-approvals.ts"

const lifecycle: ApprovalLifecycle = {
  processStreamEvent, closePermission, abandonPermissions, readPermissionRequest, permissionPendingQuestion, visibleApprovals,
}

const snapshotApproval = {
  requestId: "req-perm",
  kind: "permission",
  toolName: "Bash",
  toolUseId: "toolu_rm",
  input: { command: "rm -rf build" },
  permission: {
    toolName: "Bash", toolUseId: "toolu_rm", summary: "rm -rf build",
    input: { command: "rm -rf build" }, cwd: "T:/Projects/demo", options: ["allow_once", "allow_session", "deny"],
  },
}

// The auditor's case: the phone switched apps, the approval event arrived meanwhile. The reconnect
// walks the transcript forward (it never carried control state), so the card must come from the
// session read, as after a full reload.
test("reconnect of a loaded v2 discussion walks forward, and that walk re-reads approvals", () => {
  assert.equal(historyRevalidationDirection("v2", "cursor-1"), "after")
  assert.equal(shouldCatchUpHistory("v2", "cursor-1", false), true)

  // The client never saw the live event: no question state at all, session Active.
  const state: QuestionState | undefined = undefined
  assert.equal(mayHoldPendingApproval("Active", state), true)
  const restored = restoreApprovals(lifecycle, state, [snapshotApproval])
  const cards = visibleApprovals(restored.permissions, restored.pending)
  assert.equal(cards.length, 1)
  assert.equal(cards[0].requestId, "req-perm")
  assert.equal(restored.pending?.kind, "permission")
})

test("the catch-up re-read heals a kind-less pending and dedupes a known approval", () => {
  const healed = restoreApprovals(lifecycle, { pending: { kind: undefined, requestId: "req-perm", questions: [] } as never, outcome: null }, [snapshotApproval])
  assert.equal(healed.pending?.kind, "permission")
  assert.equal(visibleApprovals(healed.permissions, healed.pending).length, 1)
  // Running the re-read again (every reconnect, every visibility return) changes nothing.
  assert.equal(restoreApprovals(lifecycle, healed, [snapshotApproval]), healed)
})

test("an idle session with no open approval is not re-read; an open approval always is", () => {
  assert.equal(mayHoldPendingApproval("Idle", undefined), false)
  assert.equal(mayHoldPendingApproval("Stopped", { pending: null, outcome: null }), false)
  const open = restoreApprovals(lifecycle, undefined, [snapshotApproval])
  assert.equal(mayHoldPendingApproval("Idle", open), true)
})

test("every catch-up path in use-discussions reads the approvals", () => {
  const source = readFileSync(new URL("../hooks/use-discussions.ts", import.meta.url), "utf8")
  const between = (from: string, to: string) => {
    const start = source.indexOf(from)
    assert.ok(start >= 0, from)
    return source.slice(start, source.indexOf(to, start))
  }
  // Forward catch-up (reconnect, reselect) and the sessions reconcile (visibility, reconnect).
  assert.match(between("const catchUpMessages = useCallback(", "const loadEarlierMessages"), /readPendingApprovals\(id, pageSessionId\)/)
  assert.match(between("const reconcileStreaming = useCallback(", "const onVisible"), /readPendingApprovals\(d\.id, read\.sessionId\)/)
})
