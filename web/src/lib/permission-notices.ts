// Pure, so node:test can load it directly. Same shape as Code's notices for the same live event.

/**
 * A tool call the session refused without parking (an unattended session, or a
 * policy denial). Live only: the denial also reaches the transcript as that tool
 * call's result, so a reload shows the tool call and this notice is gone.
 */
export interface PermissionNotice {
  toolName: string
  reason: string
  action?: string
}

const MAX_NOTICES = 5

/** The notice a live `permission_denied` event carries: tool name plus the reason in `content`. */
export function readPermissionNotice(event: {
  toolName?: string | null
  content?: string | null
  permission?: unknown
}): PermissionNotice {
  const reason = event.content?.trim() || "No reason was given."
  const permission = event.permission
  const action = permission && typeof permission === "object" && "summary" in permission
    && typeof permission.summary === "string" ? permission.summary.trim() : undefined
  return {
    toolName: event.toolName?.trim() || "A tool call",
    reason,
    ...(action && !reason.includes(action) ? { action } : {}),
  }
}

/** Appends a notice, newest last, dropping an identical repeat and keeping the latest few. */
export function addPermissionNotice(current: readonly PermissionNotice[] | undefined, notice: PermissionNotice): PermissionNotice[] {
  const list = current ?? []
  const last = list.at(-1)
  if (last && last.toolName === notice.toolName && last.reason === notice.reason && last.action === notice.action) return list as PermissionNotice[]
  return [...list, notice].slice(-MAX_NOTICES)
}

export const PERMISSION_NOTICE_SUMMARY =
  "Action blocked because no one was available to approve it. Send a message here while signed in to enable approvals."
