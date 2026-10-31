// A message's React item for the Leaf menu: the same reactions, in the same
// order, as the picker behind the message's smiley button, so touch and the
// context menu reach exactly what the hover rail does.

import type { ContextMenuEntry } from "@redbamboo/ui"

export interface ReactionChoice {
  emoji: string
  /** Phosphor class names, already normalized. */
  icon: string
  /** The reaction entity's name; without one the label comes from the icon. */
  name?: string
}

/** "ph-light ph-hands-clapping" -> "Hands clapping". */
export function reactionLabel(choice: ReactionChoice): string {
  if (choice.name?.trim()) return choice.name.trim()
  const glyph = choice.icon.split(/\s+/).find(token => token.startsWith("ph-") && !/^ph-(thin|light|bold|fill|duotone)$/.test(token))
  if (!glyph) return choice.emoji
  const words = glyph.slice(3).replace(/-/g, " ")
  return words.charAt(0).toUpperCase() + words.slice(1)
}

export function reactionMenuEntries(choices: readonly ReactionChoice[], onAdd: (emoji: string) => void): ContextMenuEntry[] {
  if (choices.length === 0) return []
  return [{
    id: "message-react",
    label: "React",
    icon: "ph-bold ph-smiley",
    children: choices.map(choice => ({
      id: `message-react:${choice.emoji}`,
      // The icon is the reaction (entity-backed by design); the label names it.
      label: reactionLabel(choice),
      icon: choice.icon,
      run: () => onAdd(choice.emoji),
    })),
  }]
}
