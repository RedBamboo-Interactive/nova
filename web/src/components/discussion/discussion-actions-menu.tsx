import type { ReactNode } from "react"
import { DropdownMenuItem, DropdownMenuSeparator, DropdownMenuShortcut } from "@redbamboo/ui"
import { FLOATING_NOVA_SHORTCUT } from "../../lib/floating-navigation"

/** Keyboard shortcuts for the open discussion, registered as commands by the chat view. */
export const DISCUSSION_SHORTCUTS = {
  voice: "Alt+V",
  info: "Alt+I",
  rename: "Alt+R",
  updateTitle: "Alt+T",
  share: "Alt+S",
  float: FLOATING_NOVA_SHORTCUT,
  reset: "Alt+Shift+L",
  close: "Alt+W",
} as const

export interface DiscussionActionHandlers {
  onVoice?: () => void
  voiceDisabled?: boolean
  onInfo: () => void
  onRename?: () => void
  onUpdateTitle?: () => void
  updateTitlePending?: boolean
  onShare?: () => void
  onFloat?: () => void
  onReset?: () => void
  onClose?: () => void
  /** Shortcuts act on the open discussion, so only its menus advertise them. */
  showShortcuts?: boolean
}

/** The conversation actions shared by the chat header menu and each sidebar row menu.
 *  Info leads and Close sits alone at the end so a one-handed tap cannot mistake them. */
export function DiscussionActionItems({
  onVoice,
  voiceDisabled = false,
  onInfo,
  onRename,
  onUpdateTitle,
  updateTitlePending = false,
  onShare,
  onFloat,
  onReset,
  onClose,
  showShortcuts = false,
  children,
}: DiscussionActionHandlers & { children?: ReactNode }) {
  const shortcut = (keys: string) => showShortcuts && <DropdownMenuShortcut className="pl-6">{keys}</DropdownMenuShortcut>
  return (
    <>
      {onVoice && (
        <DropdownMenuItem onClick={onVoice} disabled={voiceDisabled} aria-haspopup="dialog">
          <i aria-hidden="true" className="ph-bold ph-waveform size-4" />
          Voice
          {shortcut(DISCUSSION_SHORTCUTS.voice)}
        </DropdownMenuItem>
      )}
      <DropdownMenuItem onClick={onInfo}>
        <i aria-hidden="true" className="ph-bold ph-info size-4" />
        Info
        {shortcut(DISCUSSION_SHORTCUTS.info)}
      </DropdownMenuItem>
      {onRename && (
        <DropdownMenuItem onClick={onRename}>
          <i aria-hidden="true" className="ph-bold ph-pen size-4" />
          Rename
          {shortcut(DISCUSSION_SHORTCUTS.rename)}
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
          {shortcut(DISCUSSION_SHORTCUTS.updateTitle)}
        </DropdownMenuItem>
      )}
      {onShare && (
        <DropdownMenuItem onClick={onShare}>
          <i aria-hidden="true" className="ph-bold ph-share-network size-4" />
          Share
          {shortcut(DISCUSSION_SHORTCUTS.share)}
        </DropdownMenuItem>
      )}
      {onFloat && (
        <DropdownMenuItem
          className="nova-chat-menu-float"
          onClick={onFloat}
          data-slot="floating-surface-trigger"
          data-ui-surface="nova:floating-chat"
          data-ui-action="open"
        >
          <i aria-hidden="true" className="ph-bold ph-picture-in-picture size-4" />
          Float
          {shortcut(DISCUSSION_SHORTCUTS.float)}
        </DropdownMenuItem>
      )}
      {onReset && (
        <DropdownMenuItem onClick={onReset}>
          <i aria-hidden="true" className="ph-bold ph-arrows-clockwise size-4" />
          Reset timeline
          {shortcut(DISCUSSION_SHORTCUTS.reset)}
        </DropdownMenuItem>
      )}
      {children}
      {onClose && (
        <>
          <DropdownMenuSeparator />
          <DropdownMenuItem variant="destructive" onClick={onClose}>
            <i aria-hidden="true" className="ph-bold ph-x size-4" />
            Close
            {shortcut(DISCUSSION_SHORTCUTS.close)}
          </DropdownMenuItem>
        </>
      )}
    </>
  )
}
