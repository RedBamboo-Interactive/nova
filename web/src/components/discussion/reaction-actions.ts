// A message's React item for the Leaf menu: the same reactions, in the same
// order, as the picker behind the message's smiley button, so touch and the
// context menu reach exactly what the hover rail does.

import type { ContextMenuEntry } from "@redbamboo/ui"

export interface ReactionChoice {
  emoji: string
  /** Phosphor class names, already normalized. */
  icon: string
}

export function reactionMenuEntries(choices: readonly ReactionChoice[], onAdd: (emoji: string) => void): ContextMenuEntry[] {
  if (choices.length === 0) return []
  return [{
    id: "message-react",
    label: "React",
    icon: "ph-bold ph-smiley",
    children: choices.map(choice => ({
      id: `message-react:${choice.emoji}`,
      label: choice.emoji,
      icon: choice.icon,
      run: () => onAdd(choice.emoji),
    })),
  }]
}
