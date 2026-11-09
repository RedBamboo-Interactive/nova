import { test } from "node:test"
import assert from "node:assert/strict"
import type { ChatEvent, MessageBlock, QuestionState } from "@redbamboo/chat"
import { processStreamEvent } from "../../../../redbamboo-packages/packages/chat/src/lib/process-stream-event.ts"
import { abandonPermissions, closePermission } from "../../../../redbamboo-packages/packages/chat/src/lib/permission-approval.ts"
import { answeredQuestionState, clearedQuestionState, liveQuestionFields, settleAcceptedDecision, type ApprovalLifecycle } from "./permission-approvals.ts"

// The same shared lifecycle use-discussions passes in.
const lifecycle: ApprovalLifecycle = { processStreamEvent, closePermission, abandonPermissions }

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
