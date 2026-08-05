export const FLOATING_NOVA_NAVIGATION_EVENT = "nova:floating-navigation"

export type FloatingNovaNavigationAction =
  | "show-discussions"
  | "show-chat"
  | "next-discussion"
  | "previous-discussion"
  | "new-discussion"

export const FLOATING_NOVA_SHORTCUTS = {
  showDiscussions: "Alt+ArrowLeft",
  showChat: "Alt+ArrowRight",
  nextDiscussion: "Alt+ArrowDown",
  previousDiscussion: "Alt+ArrowUp",
  newDiscussion: "Alt+N",
} as const

export const FLOATING_NOVA_SHORTCUT_LIST = Object.values(FLOATING_NOVA_SHORTCUTS).join(" ")

type NavigationKeyEvent = Pick<
  KeyboardEvent,
  "key" | "altKey" | "ctrlKey" | "metaKey" | "shiftKey" | "isComposing"
>

export function getFloatingNovaNavigationAction(event: NavigationKeyEvent): FloatingNovaNavigationAction | null {
  if (event.isComposing || event.shiftKey) return null

  if (event.altKey && !event.ctrlKey && !event.metaKey) {
    if (event.key.toLowerCase() === "n") return "new-discussion"
    if (event.key === "ArrowLeft") return "show-discussions"
    if (event.key === "ArrowRight") return "show-chat"
    if (event.key === "ArrowDown") return "next-discussion"
    if (event.key === "ArrowUp") return "previous-discussion"
    return null
  }

  return null
}
