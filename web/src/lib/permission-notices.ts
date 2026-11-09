// Pure, so node:test can load it directly. Same shape as Code's notices for the same live event.

/**
 * A tool call the session refused without parking (an unattended session, or a
 * policy denial). Live only: the denial also reaches the transcript as that tool
 * call's result, so a reload shows the tool call and this notice is gone.
 */
export interface PermissionNotice {
  toolName: string
  reason: string
}

const MAX_NOTICES = 5

/** The notice a live `permission_denied` event carries: tool name plus the reason in `content`. */
export function readPermissionNotice(event: { toolName?: string | null; content?: string | null }): PermissionNotice {
  return {
    toolName: event.toolName?.trim() || "A tool call",
    reason: event.content?.trim() || "No reason was given.",
  }
}

/** Appends a notice, newest last, dropping an identical repeat and keeping the latest few. */
export function addPermissionNotice(current: readonly PermissionNotice[] | undefined, notice: PermissionNotice): PermissionNotice[] {
  const list = current ?? []
  const last = list.at(-1)
  if (last && last.toolName === notice.toolName && last.reason === notice.reason) return list as PermissionNotice[]
  return [...list, notice].slice(-MAX_NOTICES)
}

export function permissionNoticeText(notice: PermissionNotice): string {
  return `${notice.toolName} was denied: ${notice.reason}`
}
