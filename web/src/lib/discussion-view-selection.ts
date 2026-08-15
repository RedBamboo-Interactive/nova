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
