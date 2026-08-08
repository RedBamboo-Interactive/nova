/** Follow a replacement discussion only when the rotated discussion was selected. */
export function resolveRotatedDiscussionSelection(
  selectedDiscussionId: string | null,
  oldDiscussionId: string,
  newDiscussionId: string,
): string | null {
  return selectedDiscussionId === oldDiscussionId ? newDiscussionId : selectedDiscussionId
}
