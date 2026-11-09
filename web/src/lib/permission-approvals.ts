import type {
  ChatEvent,
  MessageBlock,
  PendingQuestionKind,
  PermissionDecision,
  PermissionDecisionPayload,
  PermissionRequest,
  ProcessEventResult,
  QuestionState,
} from "@redbamboo/chat"

/**
 * The shared question lifecycle (`processStreamEvent`), passed in so this
 * module stays loadable by the node test runner, which cannot import the
 * package's React entry point.
 */
export type QuestionLifecycle = (
  messages: MessageBlock[],
  isStreaming: boolean,
  event: ChatEvent,
  resumePending: boolean,
  question: QuestionState,
) => Pick<ProcessEventResult, "question" | "isStreaming">

const NO_QUESTION: QuestionState = { pending: null, outcome: null }
const NO_MESSAGES: MessageBlock[] = []
const DECISIONS: readonly string[] = ["allow_once", "allow_session", "deny"]

/** The fields of a live `session.stream` event that carry an approval. */
export interface LiveQuestionFields {
  kind?: unknown
  permission?: unknown
  decision?: unknown
}

/**
 * Copy the approval fields of a live stream event into the ChatEvent the
 * shared lifecycle reads. Without `kind` a `question` event of kind permission
 * would open an AskUserQuestion card, so this must stay in step with ChatEvent.
 * Values outside the contract are dropped: an absent kind means "question".
 */
export function liveQuestionFields(evt: LiveQuestionFields): Pick<ChatEvent, "kind" | "permission" | "decision"> {
  const kind: PendingQuestionKind | null = evt.kind === "permission" || evt.kind === "question" ? evt.kind : null
  const permission = evt.permission && typeof evt.permission === "object" && !Array.isArray(evt.permission)
    && typeof (evt.permission as { toolName?: unknown }).toolName === "string"
    ? evt.permission as PermissionRequest
    : null
  const decision = typeof evt.decision === "string" && DECISIONS.includes(evt.decision)
    ? evt.decision as PermissionDecision
    : null
  return { kind, permission, decision }
}

/** Run one control event (question / question_resolved) through the shared lifecycle; messages are untouched. */
function control(lifecycle: QuestionLifecycle, state: QuestionState | undefined, event: ChatEvent) {
  // Hosts pass isStreaming=true, as for every live event: the lifecycle turns it
  // off while something is still pending.
  return lifecycle(NO_MESSAGES, true, event, false, state ?? NO_QUESTION)
}

/**
 * After the server took a decision: the transition its own `question_resolved`
 * makes, so the card settles even when that event is lost on a reconnect. The
 * server's event, when it comes, says the same and changes nothing. Null when
 * the approval is not open here any more (the server already settled it, or it
 * belongs to an earlier stream): committing then could resurrect a turn that
 * ended in between.
 */
export function settleAcceptedDecision(
  lifecycle: QuestionLifecycle,
  state: QuestionState | undefined,
  payload: PermissionDecisionPayload,
): Pick<ProcessEventResult, "question" | "isStreaming"> | null {
  if (!payload.requestId) return null
  const open = Object.values(state?.permissions ?? {})
    .some(approval => approval.requestId === payload.requestId && approval.outcome === null)
  if (!open) return null
  return control(lifecycle, state, {
    type: "question_resolved",
    requestId: payload.requestId,
    decision: payload.decision,
    content: payload.decision,
  })
}

/**
 * Nothing is pending any more and `outcome` says how the last AskUserQuestion
 * ended. With `keepApprovals` (the session ended, the discussion stays on
 * screen) approval cards keep how they ended, and one still waiting becomes
 * "unresolved", exactly as a terminal status event leaves them. Without it (the
 * history is gone or belongs to another session) they go with it.
 */
export function clearedQuestionState(
  lifecycle: QuestionLifecycle,
  state: QuestionState | undefined,
  outcome: QuestionState["outcome"],
  keepApprovals: boolean,
): QuestionState {
  if (!keepApprovals || !state?.permissions) return { pending: null, outcome }
  const closed = lifecycle(NO_MESSAGES, false, { type: "status", content: "idle" }, false, state).question
  return closed.permissions ? { pending: null, outcome, permissions: closed.permissions } : { pending: null, outcome }
}

/**
 * An AskUserQuestion answered from this client, cleared optimistically. With
 * no approvals in the stream this is exactly the state it always was. With
 * approvals, it is the transition the server's resolution makes: their cards
 * keep their outcomes, and one still waiting keeps the turn parked.
 */
export function answeredQuestionState(
  lifecycle: QuestionLifecycle,
  state: QuestionState | undefined,
  requestId: string | null,
): QuestionState {
  if (!state?.permissions) return { pending: null, outcome: "answered" }
  return control(lifecycle, state, { type: "question_resolved", requestId, content: "answered" }).question
}
