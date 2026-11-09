import type {
  ChatEvent,
  MessageBlock,
  PendingQuestion,
  PendingQuestionKind,
  PermissionApproval,
  PermissionApprovals,
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
  readPermissionRequest: (event: ChatEvent) => PermissionRequest
  permissionPendingQuestion: (request: PermissionRequest, requestId: string | null) => PendingQuestion
  visibleApprovals: (permissions: PermissionApprovals | null | undefined, pending: PendingQuestion | null | undefined) => PermissionApproval[]
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

/** The `/question` request a decision sends: the server-minted requestId and the decision, nothing else. */
export function permissionDecisionRequest(sessionId: string, payload: PermissionDecisionPayload): { path: string; body: { requestId: string; decision: PermissionDecision } } {
  if (!payload.requestId) throw new Error("This approval can no longer be answered here.")
  return {
    path: `/ai-session/sessions/${encodeURIComponent(sessionId)}/question`,
    body: { requestId: payload.requestId, decision: payload.decision },
  }
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value : null
}

/**
 * One `pendingQuestions` entry of a session read, when it is a tool call
 * waiting for approval (`kind: "permission"`). Everything else (an
 * AskUserQuestion above all) is not this module's business: its recovery
 * stays exactly as it was.
 */
function readSnapshotApproval(lifecycle: ApprovalLifecycle, entry: unknown): { requestId: string; request: PermissionRequest } | null {
  if (!entry || typeof entry !== "object") return null
  const record = entry as Record<string, unknown>
  if (record.kind !== "permission") return null
  const requestId = text(record.requestId)
  const permission = record.permission
  if (!requestId || !permission || typeof permission !== "object" || Array.isArray(permission)
    || !text((permission as { toolName?: unknown }).toolName)) return null
  // The shared reader, as for a live `question` event, so a restored card and
  // a live one are the same request.
  const request = lifecycle.readPermissionRequest({
    type: "question",
    kind: "permission",
    requestId,
    permission: permission as PermissionRequest,
    toolName: text(record.toolName),
    messageId: text(record.toolUseId),
  })
  return { requestId, request }
}

/**
 * Bring the approvals a session is parked on back after a reload or a
 * reconnect, from the session read's `pendingQuestions` (RedCompute overlays
 * live control state there; the transcript never holds it). Only entries of
 * kind permission are read, each with its server-minted requestId:
 *
 * - one this client already knows by requestId, open or settled, is left
 *   alone, so a resolved or expired approval never reappears;
 * - one this client holds as a plain pending question (its live event lost
 *   `kind`) becomes the approval it is, so it never opens the question popup;
 * - one it doesn't know opens, and parks the turn unless something is already
 *   pending (an AskUserQuestion keeps the composer, as it does live);
 * - one open here that the server no longer holds was resolved while this
 *   client wasn't listening: it settles as "answered", which says it was
 *   resolved without claiming how.
 *
 * Pass only a read that is current for the discussion's session: a snapshot
 * older than a live event must not be applied.
 */
export function restoreApprovals(
  lifecycle: ApprovalLifecycle,
  state: QuestionState | undefined,
  pendingQuestions: readonly unknown[],
): QuestionState {
  const snapshot = pendingQuestions
    .map(entry => readSnapshotApproval(lifecycle, entry))
    .filter((entry): entry is { requestId: string; request: PermissionRequest } => entry !== null)
  const held = new Set(snapshot.map(entry => entry.requestId))
  let next = state ?? NO_QUESTION

  // Pending here as a plain question, but the server holds that requestId as
  // an approval: a hop lost `kind` on the live event, and the person is being
  // offered a free-text "Question" for a tool call. The server's kind wins, so
  // the approval card (tool, command, the three decisions) takes its place.
  if (next.pending && next.pending.kind !== "permission" && next.pending.requestId && held.has(next.pending.requestId))
    next = { ...next, pending: null }

  for (const approval of Object.values(next.permissions ?? {}))
    if (approval.outcome === null && approval.requestId && !held.has(approval.requestId))
      next = lifecycle.closePermission(next, approval.key, "answered")

  const known = new Set(Object.values(next.permissions ?? {}).map(approval => approval.requestId))
  let restored: PendingQuestion | null = null
  for (const { requestId, request } of snapshot) {
    if (known.has(requestId)) continue
    const pending = lifecycle.permissionPendingQuestion(request, requestId)
    // The shared projection names the approval (its key) exactly as a live one.
    const approval = lifecycle.visibleApprovals(null, pending).find(candidate => candidate.requestId === requestId)
    if (!approval) continue
    next = { ...next, permissions: { ...next.permissions, [approval.key]: approval } }
    known.add(requestId)
    restored = pending
  }
  // The newest restored approval parks the turn, as the newest live one does.
  if (restored && !next.pending) next = { ...next, pending: restored }
  return next
}
