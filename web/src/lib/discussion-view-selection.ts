/**
 * A route/host selection can change one render before the discussion store's
 * local selection catches up. Never let that render expose the transcript
 * belonging to the previous discussion.
 */
export function isDiscussionSelectionCurrent(
  requestedDiscussionId: string | null,
  activeDiscussionId: string | null,
): boolean {
  return requestedDiscussionId === null || requestedDiscussionId === activeDiscussionId
}

/**
 * A click can switch the responsive pane one render before the router or Float
 * host publishes its new selection. Keep that local request authoritative only
 * for the handoff so the old transcript cannot reappear in the opened pane.
 */
export function resolveRequestedDiscussionId(
  requestedDiscussionId: string | null,
  pendingDiscussionId: string | null,
): string | null {
  return pendingDiscussionId ?? requestedDiscussionId
}
