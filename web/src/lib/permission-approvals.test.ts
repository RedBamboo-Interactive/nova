import { test } from "node:test"
import assert from "node:assert/strict"
import type { ChatEvent, MessageBlock, QuestionState } from "@redbamboo/chat"
import { processStreamEvent } from "../../../../redbamboo-packages/packages/chat/src/lib/process-stream-event.ts"
import { abandonPermissions, closePermission, permissionPendingQuestion, readPermissionRequest, visibleApprovals } from "../../../../redbamboo-packages/packages/chat/src/lib/permission-approval.ts"
import { answeredQuestionState, clearedQuestionState, liveQuestionFields, permissionDecisionRequest, restoreApprovals, settleAcceptedDecision, type ApprovalLifecycle } from "./permission-approvals.ts"

// The same shared lifecycle use-discussions passes in.
const lifecycle: ApprovalLifecycle = {
  processStreamEvent, closePermission, abandonPermissions, readPermissionRequest, permissionPendingQuestion, visibleApprovals,
}

// A Claude tool call waiting for approval, as RedCompute streams it: a `question`
// of kind permission carrying the request, then `question_resolved` with the outcome word.
const permission = {
  toolName: "Bash",
  toolUseId: "toolu_rm",
  summary: "rm -rf build",
  input: { command: "rm -rf build" },
  cwd: "T:/Projects/demo",
  options: ["allow_once", "allow_session", "deny"],
}

/** The live session.stream payload as it reaches Nova's mapper (fields RedCompute sends). */
const liveApproval = {
  type: "question",
  kind: "permission",
  requestId: "req-perm",
  toolName: "Bash",
  toolInput: JSON.stringify(permission.input),
  messageId: "toolu_rm",
  permission,
}

/** The subset of Nova's session.stream -> ChatEvent mapping that the lifecycle reads. */
function mapLive(evt: Record<string, unknown>): ChatEvent {
  return {
    type: evt.type as ChatEvent["type"],
    content: (evt.content as string | undefined) ?? null,
    toolName: (evt.toolName as string | undefined) ?? null,
    toolInput: (evt.toolInput as string | undefined) ?? null,
    messageId: (evt.messageId as string | undefined) ?? null,
    requestId: (evt.requestId as string | undefined) ?? null,
    ...liveQuestionFields(evt),
  }
}

const toolUse: ChatEvent = {
  type: "tool_use", toolName: "Bash", toolInput: JSON.stringify(permission.input), messageId: "toolu_rm", messageUid: "turn-1",
}

function replay(events: ChatEvent[], start: QuestionState = { pending: null, outcome: null }) {
  let messages: MessageBlock[] = []
  let question = start
  let isStreaming = true
  for (const event of events) {
    const result = processStreamEvent(messages, true, event, false, question)
    messages = result.messages
    question = result.question
    isStreaming = result.isStreaming
  }
  return { messages, question, isStreaming }
}

test("live mapping keeps kind and the request, so an approval opens as an approval", () => {
  const { question, isStreaming } = replay([toolUse, mapLive(liveApproval)])
  assert.equal(question.pending?.kind, "permission")
  assert.equal(question.pending?.requestId, "req-perm")
  assert.equal(question.pending?.permission?.summary, "rm -rf build")
  assert.equal(question.permissions?.toolu_rm?.outcome, null)
  // An AskUserQuestion's outcome is untouched, and the turn is parked.
  assert.equal(question.outcome, null)
  assert.equal(isStreaming, false)
})

test("without kind the same event stays an ordinary AskUserQuestion (the regression guard)", () => {
  const { kind: _kind, permission: _permission, ...withoutKind } = liveApproval
  const { question } = replay([toolUse, mapLive(withoutKind)])
  assert.notEqual(question.pending?.kind, "permission")
  assert.equal(question.permissions, undefined)
})

test("only contract values cross the mapper", () => {
  assert.deepEqual(liveQuestionFields({}), { kind: null, permission: null, decision: null })
  assert.deepEqual(liveQuestionFields({ kind: "approval", permission: "Bash", decision: "maybe" }),
    { kind: null, permission: null, decision: null })
  assert.deepEqual(liveQuestionFields({ kind: "question" }).kind, "question")
  assert.equal(liveQuestionFields({ decision: "allow_session" }).decision, "allow_session")
  assert.equal(liveQuestionFields({ permission: { summary: "no tool" } }).permission, null)
})

test("an accepted decision settles the card at once, and the server's own resolution agrees", () => {
  const parked = replay([toolUse, mapLive(liveApproval)]).question
  const settled = settleAcceptedDecision(lifecycle, parked, { requestId: "req-perm", toolUseId: "toolu_rm", decision: "allow_once" })
  assert.ok(settled)
  assert.equal(settled.question.pending, null)
  assert.equal(settled.question.permissions?.toolu_rm?.outcome, "allow_once")
  assert.equal(settled.isStreaming, true)

  // RedCompute then says "allowed_once": nothing changes.
  const server = replay([mapLive({ type: "question_resolved", requestId: "req-perm", content: "allowed_once" })], settled.question)
  assert.equal(server.question.permissions?.toolu_rm?.outcome, "allow_once")
  assert.equal(server.question.pending, null)

  // And the tool's own result keeps the outcome (only the waiting call's result settles it).
  const result = replay([{ type: "tool_result", toolResult: "ok", messageId: "toolu_rm" }], server.question)
  assert.equal(result.question.permissions?.toolu_rm?.outcome, "allow_once")
})

test("a decision the server already resolved is not committed again", () => {
  const parked = replay([toolUse, mapLive(liveApproval)]).question
  const resolved = replay([mapLive({ type: "question_resolved", requestId: "req-perm", content: "denied" })], parked).question
  assert.equal(resolved.permissions?.toolu_rm?.outcome, "deny")
  assert.equal(settleAcceptedDecision(lifecycle, resolved, { requestId: "req-perm", toolUseId: "toolu_rm", decision: "allow_once" }), null)
  assert.equal(settleAcceptedDecision(lifecycle, undefined, { requestId: "req-perm", toolUseId: null, decision: "deny" }), null)
  assert.equal(settleAcceptedDecision(lifecycle, parked, { requestId: null, toolUseId: "toolu_rm", decision: "deny" }), null)
})

test("answering an AskUserQuestion without approvals is exactly the old optimistic clear", () => {
  const askState: QuestionState = { pending: { question: "Which database?", requestId: "req-ask" }, outcome: null }
  assert.deepEqual(answeredQuestionState(lifecycle, askState, "req-ask"), { pending: null, outcome: "answered" })
  assert.deepEqual(answeredQuestionState(lifecycle, undefined, null), { pending: null, outcome: "answered" })
})

test("answering an AskUserQuestion keeps an approval opened under it, and the turn stays parked on it", () => {
  const ask: ChatEvent = { type: "question", requestId: "req-ask", toolName: "AskUserQuestion",
    toolInput: JSON.stringify({ questions: [{ question: "Which database?", options: [{ label: "Postgres" }] }] }) }
  const both = replay([toolUse, ask, mapLive(liveApproval)]).question
  // The question keeps the composer; the approval waits on its card.
  assert.equal(both.pending?.requestId, "req-ask")
  assert.equal(both.permissions?.toolu_rm?.outcome, null)

  const answered = answeredQuestionState(lifecycle, both, "req-ask")
  assert.equal(answered.outcome, "answered")
  assert.equal(answered.pending?.kind, "permission")
  assert.equal(answered.permissions?.toolu_rm?.outcome, null)
})

test("a session that ended leaves settled cards and marks a waiting one unresolved; a replaced history drops them", () => {
  const parked = replay([toolUse, mapLive(liveApproval)]).question
  const kept = clearedQuestionState(lifecycle, parked, null, true)
  assert.equal(kept.pending, null)
  assert.equal(kept.outcome, null)
  assert.equal(kept.permissions?.toolu_rm?.outcome, "unresolved")

  assert.deepEqual(clearedQuestionState(lifecycle, parked, "session_ended", false), { pending: null, outcome: "session_ended" })
  // No approvals: the exact state clearQuestion always wrote.
  assert.deepEqual(clearedQuestionState(lifecycle, { pending: { question: "q", requestId: "r" }, outcome: null }, "session_ended", true),
    { pending: null, outcome: "session_ended" })
})

// ---- Restoring waiting approvals from a session read (reload, reconnect) ----

/** The pendingQuestions entry RedCompute's session read returns for the parked approval above. */
const snapshotApproval = {
  requestId: "req-perm",
  kind: "permission",
  permission,
  toolName: "Bash",
  toolUseId: "toolu_rm",
  questions: [],
  input: permission.input,
  askedAt: "2026-11-09T12:00:00Z",
  expiresAt: "2026-11-09T12:30:00Z",
}

const snapshotQuestion = {
  requestId: "req-ask",
  toolName: "AskUserQuestion",
  toolUseId: "toolu_ask",
  questions: [{ question: "Which database?", options: [{ label: "Postgres" }] }],
  input: {},
  askedAt: "2026-11-09T12:00:00Z",
  expiresAt: "2026-11-09T12:30:00Z",
}

/** What ChatPanel draws: visibleApprovals over the host's approvals and pending question. */
function drawn(state: QuestionState) {
  return visibleApprovals(state.permissions, state.pending)
}

test("a stream that drops after the approval opened gets the card back from the snapshot, with the same requestId", () => {
  // Live: the card opens, then the socket drops (the host forgets all question state).
  const live = replay([toolUse, mapLive(liveApproval)]).question
  assert.equal(drawn(live)[0]?.requestId, "req-perm")
  const afterDrop: QuestionState | undefined = undefined

  // Remount: the history load reads the session, whose pendingQuestions still holds it.
  const restored = restoreApprovals(lifecycle, afterDrop, [snapshotApproval])
  const [card] = drawn(restored)
  assert.equal(drawn(restored).length, 1)
  assert.equal(card.requestId, "req-perm")
  assert.equal(card.outcome, null)
  assert.equal(card.request.toolUseId, "toolu_rm")
  assert.equal(card.request.summary, "rm -rf build")
  assert.equal(card.key, live.permissions?.toolu_rm?.key)
  // It parks the turn as an approval, never as an AskUserQuestion.
  assert.equal(restored.pending?.kind, "permission")
  assert.equal(restored.pending?.requestId, "req-perm")
  assert.equal(restored.outcome, null)

  // A decision on that card posts the same server-minted requestId.
  const payload = { requestId: card.requestId, toolUseId: card.request.toolUseId ?? null, decision: "allow_session" as const }
  assert.deepEqual(permissionDecisionRequest("session-1", payload), {
    path: "/ai-session/sessions/session-1/question",
    body: { requestId: "req-perm", decision: "allow_session" },
  })

  // Accepted, it settles; the server's resolution and the tool result agree.
  const settled = settleAcceptedDecision(lifecycle, restored, payload)
  assert.ok(settled)
  assert.equal(settled.question.permissions?.toolu_rm?.outcome, "allow_session")
  assert.equal(settled.question.pending, null)
  const resolved = replay([
    toolUse,
    mapLive({ type: "question_resolved", requestId: "req-perm", content: "allowed_session" }),
    { type: "tool_result", toolResult: "ok", messageId: "toolu_rm" },
  ], settled.question).question
  assert.equal(resolved.permissions?.toolu_rm?.outcome, "allow_session")
  assert.equal(resolved.pending, null)
})

test("a snapshot without the approval shows nothing", () => {
  const restored = restoreApprovals(lifecycle, undefined, [])
  assert.deepEqual(drawn(restored), [])
  assert.equal(restored.pending, null)
  assert.equal(restored.permissions, undefined)
})

test("only approvals are restored: an AskUserQuestion in the snapshot is left to its own recovery", () => {
  assert.deepEqual(restoreApprovals(lifecycle, undefined, [snapshotQuestion]), { pending: null, outcome: null })
  // Malformed approval entries are ignored too.
  assert.deepEqual(restoreApprovals(lifecycle, undefined, [
    { ...snapshotApproval, requestId: "" },
    { ...snapshotApproval, permission: null },
    { ...snapshotApproval, permission: { summary: "no tool" } },
    "req-perm",
  ]), { pending: null, outcome: null })
})

test("an AskUserQuestion already pending keeps the composer; the restored approval waits on its card", () => {
  const asking: QuestionState = { pending: { question: "Which database?", requestId: "req-ask" }, outcome: null }
  const restored = restoreApprovals(lifecycle, asking, [snapshotQuestion, snapshotApproval])
  assert.equal(restored.pending, asking.pending)
  assert.equal(restored.outcome, null)
  assert.equal(drawn(restored).length, 1)
  assert.equal(drawn(restored)[0].requestId, "req-perm")
})

test("a resolved or expired approval never reappears, even from a stale snapshot", () => {
  const parked = replay([toolUse, mapLive(liveApproval)]).question
  const denied = replay([mapLive({ type: "question_resolved", requestId: "req-perm", content: "denied" })], parked).question
  const restored = restoreApprovals(lifecycle, denied, [snapshotApproval])
  assert.equal(restored, denied)
  assert.equal(restored.permissions?.toolu_rm?.outcome, "deny")
  assert.equal(restored.pending, null)
})

test("an approval the server no longer holds was resolved while away: it settles and the turn unparks", () => {
  const parked = replay([toolUse, mapLive(liveApproval)]).question
  const restored = restoreApprovals(lifecycle, parked, [])
  assert.equal(restored.permissions?.toolu_rm?.outcome, "answered")
  assert.equal(restored.pending, null)
  // A snapshot that still holds it changes nothing.
  assert.equal(restoreApprovals(lifecycle, parked, [snapshotApproval]), parked)
})

test("a decision without a requestId is refused before anything is posted", () => {
  assert.throws(() => permissionDecisionRequest("s", { requestId: null, toolUseId: "toolu_rm", decision: "deny" }), /no longer be answered/)
})
