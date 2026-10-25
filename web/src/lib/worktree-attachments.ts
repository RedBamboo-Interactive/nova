import type { WorktreeAttachment } from "./types"

/** `repo · branch`, the one label a worktree carries everywhere it is shown. */
export function placementLabel(repository: string | null | undefined, branch: string | null | undefined): string {
  return [repository, branch].filter((part): part is string => typeof part === "string" && part.length > 0).join(" · ")
}

export function worktreeChipLabel(attachment: Pick<WorktreeAttachment, "repositoryName" | "branch">): string {
  return placementLabel(attachment.repositoryName, attachment.branch)
}

/**
 * Where Code shows the repository, filtered to the attachment's branch (the route's `branch`
 * deep link). Code keys the page by repository id; the folder path is not part of its routes.
 */
export function worktreeCodePath(attachment: Pick<WorktreeAttachment, "repositoryId" | "branch">): string | undefined {
  if (!attachment.repositoryId) return undefined
  const branch = attachment.branch ? `?branch=${encodeURIComponent(attachment.branch)}` : ""
  return `/apps/codered/github/${encodeURIComponent(attachment.repositoryId)}${branch}`
}

/** Placement of a delegated session as its marker or activity row records it. Old rows carry neither field. */
export function delegationPlacement(data: Record<string, unknown> | null | undefined):
  { label: string; worktree: boolean } | undefined {
  const text = (value: unknown) => typeof value === "string" && value.trim() ? value.trim() : undefined
  const repository = text(data?.repository)
  const branch = text(data?.branch)
  if (!repository && !branch) return undefined
  return { label: placementLabel(repository, branch), worktree: text(data?.worktreePath) !== undefined }
}

/** Tolerates a server that predates attachments, or a malformed entry. */
export function readWorktrees(value: unknown): WorktreeAttachment[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is WorktreeAttachment =>
    !!item && typeof item === "object"
    && typeof (item as WorktreeAttachment).path === "string"
    && typeof (item as WorktreeAttachment).branch === "string")
}
