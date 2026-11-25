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
  /** Opens the Skill picker: a Skill for this discussion only. */
  onAttachSkill?: () => void
  /** Opens the MCP server picker: its tools for this discussion only. */
  onAttachMcpServer?: () => void
  onFloat?: () => void
  onReset?: () => void
}

/** One layout for the discussion header, sidebar actions, and context menu. */
export function discussionActionEntries({ onVoice, voiceDisabled = false, onAttachSkill, onAttachMcpServer, onFloat, onReset, ...handlers }: DiscussionActionHandlers): ContextMenuEntry[] {
  const shortcut = (keys: string) => conversationShortcut(keys, handlers.showShortcuts)
  const session: ContextMenuEntry[] = []
  if (onVoice) {
    session.push({
      id: "voice", label: "Voice", icon: "ph-bold ph-waveform", disabled: voiceDisabled,
      shortcut: shortcut(DISCUSSION_SHORTCUTS.voice), run: () => onVoice(), attributes: { "aria-haspopup": "dialog" },
    })
  }
  if (onAttachSkill) {
    session.push({
      id: "attach-skill", label: "Attach Skill…", icon: "ph-bold ph-puzzle-piece",
      run: () => onAttachSkill(), attributes: { "aria-haspopup": "dialog" },
    })
  }
  if (onAttachMcpServer) {
    session.push({
      id: "attach-mcp-server", label: "Attach MCP server…", icon: "ph-bold ph-plugs-connected",
      run: () => onAttachMcpServer(), attributes: { "aria-haspopup": "dialog" },
    })
  }
  const windowActions: ContextMenuEntry[] = []
  if (onFloat) {
    windowActions.push({
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
  const common = new Map(conversationActionEntries(handlers).map((entry) => [entry.id, entry]))
  const entriesFor = (...ids: string[]) => ids.flatMap((id) => {
    const entry = common.get(id)
    return entry ? [entry] : []
  })

  const footer = entriesFor("stop", "close")
  if (onReset) {
    footer.unshift({ id: "reset", label: "Reset timeline", icon: "ph-bold ph-arrows-clockwise", shortcut: shortcut(DISCUSSION_SHORTCUTS.reset), run: () => onReset() })
  }

  const entries: ContextMenuEntry[] = [{
    id: "discussion-actions",
    label: "Discussion",
    group: true,
    children: entriesFor("info", "artifacts", "share", "rename", "update-title"),
  }]
  if (session.length) {
    entries.push(
      { id: "session-separator", separator: true },
      { id: "session-actions", label: "Session", group: true, children: session },
    )
  }
  if (windowActions.length) entries.push({ id: "window-separator", separator: true }, ...windowActions)
  if (footer.length) entries.push({ id: "footer-separator", separator: true }, ...footer)
  return entries
}
