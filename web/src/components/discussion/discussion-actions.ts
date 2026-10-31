// Descriptor builder only (no runtime imports), so node tests load it directly.
import type { ContextMenuEntry } from "@redbamboo/ui"
import { FLOATING_NOVA_SHORTCUT } from "../../lib/floating-navigation.ts"

/** Keyboard shortcuts for the open discussion, registered as commands by the chat view. */
export const DISCUSSION_SHORTCUTS = {
  voice: "Alt+V",
  info: "Alt+I",
  artifacts: "Alt+A",
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
  /** Opens the Artifacts panel for the discussion. */
  onArtifacts?: () => void
  onRename?: () => void
  onUpdateTitle?: () => void
  updateTitlePending?: boolean
  onShare?: () => void
  onFloat?: () => void
  onReset?: () => void
  /** Stops the session process to free memory; the next send resumes it. */
  onStop?: () => void
  onClose?: () => void
  /** Shortcuts act on the open discussion, so only its menus advertise them. */
  showShortcuts?: boolean
}

/** The conversation actions shared by the chat header menu, each sidebar row menu and the
 *  rows' context menu: one descriptor array, so the menus cannot drift.
 *  Info leads, Artifacts follows it, and Close sits alone at the end so a one-handed tap cannot mistake them. */
export function discussionActionEntries({
  onVoice,
  voiceDisabled = false,
  onInfo,
  onArtifacts,
  onRename,
  onUpdateTitle,
  updateTitlePending = false,
  onShare,
  onFloat,
  onReset,
  onStop,
  onClose,
  showShortcuts = false,
}: DiscussionActionHandlers): ContextMenuEntry[] {
  const shortcut = (keys: string) => (showShortcuts ? keys : undefined)
  const entries: ContextMenuEntry[] = []
  if (onVoice) {
    entries.push({
      id: "voice", label: "Voice", icon: "ph-bold ph-waveform", disabled: voiceDisabled,
      shortcut: shortcut(DISCUSSION_SHORTCUTS.voice), run: () => onVoice(), attributes: { "aria-haspopup": "dialog" },
    })
  }
  entries.push({ id: "info", label: "Info", icon: "ph-bold ph-info", shortcut: shortcut(DISCUSSION_SHORTCUTS.info), run: () => onInfo() })
  if (onArtifacts) entries.push({ id: "artifacts", label: "Artifacts", icon: "ph-bold ph-squares-four", shortcut: shortcut(DISCUSSION_SHORTCUTS.artifacts), run: () => onArtifacts() })
  if (onRename) entries.push({ id: "rename", label: "Rename", icon: "ph-bold ph-pen", shortcut: shortcut(DISCUSSION_SHORTCUTS.rename), run: () => onRename() })
  if (onUpdateTitle) {
    entries.push({
      id: "update-title",
      label: "Update title",
      icon: updateTitlePending ? "ph-bold ph-spinner-gap animate-spin" : "ph-bold ph-sparkle",
      disabled: updateTitlePending,
      shortcut: shortcut(DISCUSSION_SHORTCUTS.updateTitle),
      run: () => onUpdateTitle(),
      attributes: { "aria-busy": updateTitlePending },
    })
  }
  if (onShare) entries.push({ id: "share", label: "Share", icon: "ph-bold ph-share-network", shortcut: shortcut(DISCUSSION_SHORTCUTS.share), run: () => onShare() })
  if (onFloat) {
    entries.push({
      id: "float",
      label: "Float",
      icon: "ph-bold ph-picture-in-picture",
      shortcut: shortcut(DISCUSSION_SHORTCUTS.float),
      // Runs inside the click: opening Document Picture-in-Picture needs the user activation.
      run: () => onFloat(),
      className: "nova-chat-menu-float",
      attributes: { "data-slot": "floating-surface-trigger", "data-ui-surface": "nova:floating-chat", "data-ui-action": "open" },
    })
  }
  if (onReset) entries.push({ id: "reset", label: "Reset timeline", icon: "ph-bold ph-arrows-clockwise", shortcut: shortcut(DISCUSSION_SHORTCUTS.reset), run: () => onReset() })
  if (onStop) entries.push({ id: "stop", label: "Stop", icon: "ph-bold ph-stop", run: () => onStop() })
  if (onClose) {
    entries.push(
      { id: "close-separator", separator: true },
      { id: "close", label: "Close", icon: "ph-bold ph-x", danger: true, shortcut: shortcut(DISCUSSION_SHORTCUTS.close), run: () => onClose() },
    )
  }
  return entries
}

