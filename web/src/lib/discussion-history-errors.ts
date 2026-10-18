export interface HistoryLoadFailure {
  message: string
  code: string | null
}

export interface DiscussionHistoryFailures {
  initial?: HistoryLoadFailure
  earlier?: HistoryLoadFailure
}

export type DiscussionHistoryFailureState = Record<string, DiscussionHistoryFailures>
export type HistoryLoadFailureScope = "initial" | "earlier"
export type HistoryPageCommit = "committed" | "stale" | "invalid"

export const INVALID_HISTORY_PAGE_FAILURE: HistoryLoadFailure = {
  message: "Nova received history it couldn't verify. Try again.",
  code: "history_page_invalid",
}

/**
 * Pager rejections caused by the page itself rather than by a newer request.
 * A superseded request is safe to drop; an unverifiable page never is.
 */
export function isInvalidHistoryPage(rejection: string | undefined): boolean {
  return rejection === "invalid-page" || rejection === "incomplete-boundary"
}

function readableServerMessage(error: unknown): string | null {
  if (!(error instanceof Error)) return null
  const code = "code" in error && typeof error.code === "string" ? error.code : null
  const message = error.message.trim()
  if (!message || message === code || /^[a-z0-9_]+$/.test(message)) return null
  return message
}

/** Keep transport details out of the empty transcript while preserving server guidance. */
export function describeHistoryLoadFailure(error: unknown): HistoryLoadFailure {
  const code = error instanceof Error && "code" in error && typeof error.code === "string"
    ? error.code
    : null
  const serverMessage = readableServerMessage(error)
  if (serverMessage) return { message: serverMessage, code }
  if (code === "transcript_message_too_large") {
    return { message: "This discussion is too large to load right now.", code }
  }
  if (code === "redleaf_unavailable") {
    return { message: "Discussion history is temporarily unavailable.", code }
  }
  return { message: "Check your connection and try again.", code }
}

export function recordHistoryLoadFailure(
  state: DiscussionHistoryFailureState,
  discussionId: string,
  scope: HistoryLoadFailureScope,
  failure: HistoryLoadFailure,
  durableWindowLoaded = false,
): DiscussionHistoryFailureState {
  // A failed refresh over an already loaded durable window stays quiet. Blocks
  // that only arrived over the live stream are not a transcript and still need
  // a visible retry, or the discussion silently starts mid-turn.
  if (scope === "initial" && durableWindowLoaded) return state
  return {
    ...state,
    [discussionId]: { ...state[discussionId], [scope]: failure },
  }
}

export function clearHistoryLoadFailure(
  state: DiscussionHistoryFailureState,
  discussionId: string,
  scope?: HistoryLoadFailureScope,
): DiscussionHistoryFailureState {
  const current = state[discussionId]
  if (!current) return state
  if (!scope) {
    const next = { ...state }
    delete next[discussionId]
    return next
  }
  if (!current[scope]) return state
  const entry = { ...current }
  delete entry[scope]
  const next = { ...state }
  if (entry.initial || entry.earlier) next[discussionId] = entry
  else delete next[discussionId]
  return next
}
