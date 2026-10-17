import type { ReactNode } from "react"
import { DropdownMenuItem, DropdownMenuSeparator } from "@redbamboo/ui"

export interface DiscussionActionHandlers {
  onInfo: () => void
  onRename?: () => void
  onUpdateTitle?: () => void
  updateTitlePending?: boolean
  onShare?: () => void
  onFloat?: () => void
  onClose?: () => void
}

/** The conversation actions shared by the chat header menu and each sidebar row menu.
 *  Info leads and Close sits alone at the end so a one-handed tap cannot mistake them. */
export function DiscussionActionItems({
  onInfo,
  onRename,
  onUpdateTitle,
  updateTitlePending = false,
  onShare,
  onFloat,
  onClose,
  children,
}: DiscussionActionHandlers & { children?: ReactNode }) {
  return (
    <>
      <DropdownMenuItem onClick={onInfo}>
        <i aria-hidden="true" className="ph-bold ph-info size-4" />
        Info
      </DropdownMenuItem>
      {onRename && (
        <DropdownMenuItem onClick={onRename}>
          <i aria-hidden="true" className="ph-bold ph-pen size-4" />
          Rename
        </DropdownMenuItem>
      )}
      {onUpdateTitle && (
        <DropdownMenuItem
          onClick={onUpdateTitle}
          disabled={updateTitlePending}
          aria-busy={updateTitlePending}
        >
          <i aria-hidden="true" className={`${updateTitlePending ? "ph-bold ph-spinner-gap animate-spin" : "ph-bold ph-sparkle"} size-4`} />
          Update title
        </DropdownMenuItem>
      )}
      {onShare && (
        <DropdownMenuItem onClick={onShare}>
          <i aria-hidden="true" className="ph-bold ph-share-network size-4" />
          Share
        </DropdownMenuItem>
      )}
      {onFloat && (
        <DropdownMenuItem
          className="nova-chat-menu-float"
          onClick={onFloat}
          data-slot="floating-surface-trigger"
          data-ui-surface="nova:floating-chat"
          data-ui-action="open"
          title="Float Nova (Ctrl+Alt+N)"
        >
          <i aria-hidden="true" className="ph-bold ph-picture-in-picture size-4" />
          Float
        </DropdownMenuItem>
      )}
      {children}
      {onClose && (
        <>
          <DropdownMenuSeparator />
          <DropdownMenuItem variant="destructive" onClick={onClose}>
            <i aria-hidden="true" className="ph-bold ph-x size-4" />
            Close
          </DropdownMenuItem>
        </>
      )}
    </>
  )
}
