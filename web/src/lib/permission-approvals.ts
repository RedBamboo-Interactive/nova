import type {
  ChatEvent,
  MessageBlock,
  PendingQuestionKind,
  PermissionDecision,
  PermissionDecisionPayload,
  PermissionOutcome,
  PermissionRequest,
  ProcessEventResult,
  QuestionState,
} from "@redbamboo/chat"

/**
 * The shared approval lifecycle from `@redbamboo/chat`, passed in so this
 * module stays loadable by the node test runner, which cannot import the
 * package's React entry point. Nova reuses it rather than copying it.
 */
export interface ApprovalLifecycle {
  processStreamEvent: (
    messages: MessageBlock[],
    isStreaming: boolean,
    event: ChatEvent,
    resumePending: boolean,
    question: QuestionState,
  ) => Pick<ProcessEventResult, "question">
  closePermission: (prev: QuestionState, key: string, outcome: PermissionOutcome) => QuestionState
  abandonPermissions: (state: QuestionState) => QuestionState
}

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

/**
 * After the server took a decision: settle that approval with the decision,
 * so the card settles even when the server's `question_resolved` is lost on a
 * reconnect (that event, when it comes, says the same). `isStreaming`: nothing
 * is left waiting, so the turn runs again. Null when the approval is not open
 * here any more (the server already settled it, or it belongs to an earlier
 * stream): committing then could resurrect a turn that ended in between.
 */
export function settleAcceptedDecision(
  lifecycle: ApprovalLifecycle,
  state: QuestionState | undefined,
  payload: PermissionDecisionPayload,
): { question: QuestionState; isStreaming: boolean } | null {
  if (!payload.requestId || !state) return null
  const open = Object.values(state.permissions ?? {})
    .find(approval => approval.requestId === payload.requestId && approval.outcome === null)
  if (!open) return null
  const question = lifecycle.closePermission(state, open.key, payload.decision)
  return { question, isStreaming: !question.pending }
}

/**
 * Nothing is pending any more and `outcome` says how the last AskUserQuestion
 * ended. With `keepApprovals` (the session ended, the discussion stays on
 * screen) approval cards keep how they ended, and one still waiting becomes
 * "unresolved". Without it (the history is gone or belongs to another session)
 * they go with it.
 */
export function clearedQuestionState(
  lifecycle: ApprovalLifecycle,
  state: QuestionState | undefined,
  outcome: QuestionState["outcome"],
  keepApprovals: boolean,
): QuestionState {
  if (!keepApprovals || !state?.permissions) return { pending: null, outcome }
  const { permissions } = lifecycle.abandonPermissions(state)
  return permissions ? { pending: null, outcome, permissions } : { pending: null, outcome }
}

/**
 * An AskUserQuestion answered from this client, cleared optimistically. With
 * no approvals in the stream this is exactly the state it always was. With
 * approvals, it is the transition the server's resolution makes, run through
 * the shared lifecycle: their cards keep their outcomes, and one still
 * waiting keeps the turn parked.
 */
export function answeredQuestionState(
  lifecycle: ApprovalLifecycle,
  state: QuestionState | undefined,
  requestId: string | null,
): QuestionState {
  if (!state?.permissions) return { pending: null, outcome: "answered" }
  return lifecycle.processStreamEvent(NO_MESSAGES, true,
    { type: "question_resolved", requestId, content: "answered" }, false, state ?? NO_QUESTION).question
}
