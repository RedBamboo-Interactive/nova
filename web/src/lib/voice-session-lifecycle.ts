import { isMobileClient, type FloatingNovaNavigator } from "../components/floating-nova-support.ts"

/**
 * Mobile browsers suspend or evict background pages aggressively, so hiding the
 * page is a billing boundary there. Desktop visibility changes also cover normal
 * Alt+Tab and tab switching, which must leave an intentional call connected.
 */
export function shouldCloseVoiceWhenHidden(
  visibilityState: DocumentVisibilityState,
  targetNavigator?: FloatingNovaNavigator,
): boolean {
  return visibilityState === "hidden" && isMobileClient(targetNavigator)
}
