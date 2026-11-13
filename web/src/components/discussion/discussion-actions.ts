// Descriptor builder only (no runtime imports), so node tests load it directly.
import type { ContextMenuEntry } from "@redbamboo/ui"
import {
  CONVERSATION_SHORTCUTS,
  conversationActionEntries,
  conversationShortcut,
  type ConversationActionHandlers,
} from "@redbamboo/chat/conversation-actions"
import { FLOATING_NOVA_SHORTCUT } from "../../lib/floating-navigation.ts"

/**
 * Keyboard shortcuts for the open discussion: the shared conversation map plus Nova's own Voice,
 * Float and Reset timeline, registered as commands by the chat view.
 */
export const DISCUSSION_SHORTCUTS = {
  voice: "Alt+V",
  ...CONVERSATION_SHORTCUTS,
  float: FLOATING_NOVA_SHORTCUT,
  reset: "Alt+Shift+L",
} as const

export interface DiscussionActionHandlers extends Omit<ConversationActionHandlers, "leading" | "afterShare" | "trailing"> {
  onVoice?: () => void
  voiceDisabled?: boolean
  onFloat?: () => void
  onReset?: () => void
}

/**
 * Nova's conversation actions: the shared builder (Info, Artifacts, Rename, Update title, Share,
 * Stop, Close) with Nova's entries in its slots. Voice leads, Float and Reset timeline follow Share.
 * The header menu, each sidebar row menu and the rows' context menu render this one array.
 */
export function discussionActionEntries({ onVoice, voiceDisabled = false, onFloat, onReset, ...handlers }: DiscussionActionHandlers): ContextMenuEntry[] {
  const shortcut = (keys: string) => conversationShortcut(keys, handlers.showShortcuts)
  const leading: ContextMenuEntry[] = []
  if (onVoice) {
    leading.push({
      id: "voice", label: "Voice", icon: "ph-bold ph-waveform", disabled: voiceDisabled,
      shortcut: shortcut(DISCUSSION_SHORTCUTS.voice), run: () => onVoice(), attributes: { "aria-haspopup": "dialog" },
    })
  }
  const afterShare: ContextMenuEntry[] = []
  if (onFloat) {
    afterShare.push({
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
  if (onReset) afterShare.push({ id: "reset", label: "Reset timeline", icon: "ph-bold ph-arrows-clockwise", shortcut: shortcut(DISCUSSION_SHORTCUTS.reset), run: () => onReset() })
  return conversationActionEntries({ ...handlers, leading, afterShare })
}
