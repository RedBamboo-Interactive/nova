import { ActionMenuItems } from "@redbamboo/ui"
import { discussionActionEntries, type DiscussionActionHandlers } from "./discussion-actions.ts"

export { DISCUSSION_SHORTCUTS, discussionActionEntries, type DiscussionActionHandlers } from "./discussion-actions.ts"

/** Renders {@link discussionActionEntries} inside a menu popup. */
export function DiscussionActionItems(handlers: DiscussionActionHandlers) {
  return <ActionMenuItems entries={discussionActionEntries(handlers)} />
}
