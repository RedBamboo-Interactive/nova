import { useNavigate } from "react-router-dom"
import type { WorktreeAttachment } from "../../lib/types"
import { worktreeChipLabel, worktreeCodePath } from "../../lib/worktree-attachments"

/**
 * The worktrees this discussion works in, one chip each: `repo · branch`. The discussion keeps its
 * Agent workspace; a chip only says where it touches a repository, and opens that repository in
 * Code on the branch.
 */
export function WorktreeChips({ worktrees }: { worktrees: readonly WorktreeAttachment[] | undefined }) {
  const navigate = useNavigate()
  if (!worktrees?.length) return null
  return (
    <div
      data-slot="discussion-worktrees"
      role="list"
      aria-label="Worktrees this discussion works in"
      className="flex shrink-0 flex-wrap items-center gap-1.5 border-b border-overlay-6 px-4 py-1.5"
    >
      {worktrees.map((worktree) => {
        const path = worktreeCodePath(worktree)
        return (
          <button
            key={worktree.path}
            type="button"
            role="listitem"
            data-slot="discussion-worktree-chip"
            disabled={!path}
            onClick={() => path && navigate(path)}
            title={`${worktree.path}\nOpen in Code`}
            className="inline-flex max-w-full items-center gap-1.5 rounded-md bg-overlay-6 px-2 py-1 text-[11px] font-medium text-text-secondary transition-colors hover:bg-overlay-10 hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 disabled:cursor-default disabled:hover:bg-overlay-6 disabled:hover:text-text-secondary"
          >
            <i aria-hidden="true" className="ph-bold ph-git-branch shrink-0 text-[12px]" />
            <span className="min-w-0 truncate">{worktreeChipLabel(worktree)}</span>
          </button>
        )
      })}
    </div>
  )
}
