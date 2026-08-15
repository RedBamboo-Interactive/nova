export interface DocumentPictureInPictureController {
  readonly window: Window | null
  requestWindow(options?: { width?: number; height?: number; preferInitialWindowPlacement?: boolean }): Promise<Window>
}

export interface FloatingNovaWindow {
  readonly top: unknown
  readonly documentPictureInPicture?: DocumentPictureInPictureController
}

export interface FloatingNovaNavigator {
  readonly userAgent: string
  readonly userAgentData?: { readonly mobile?: boolean }
}

export interface FloatingNovaSupport {
  supported: boolean
  reason?: string
}

export function isMobileClient(
  targetNavigator: FloatingNovaNavigator | undefined = typeof navigator === "undefined"
    ? undefined
    : navigator as Navigator & FloatingNovaNavigator,
): boolean {
  return targetNavigator?.userAgentData?.mobile === true
    || /Android|iPhone|iPad|iPod|Mobile/i.test(targetNavigator?.userAgent ?? "")
}

export function getFloatingNovaSupport(
  targetWindow: FloatingNovaWindow | undefined = typeof window === "undefined"
    ? undefined
    : window as Window & FloatingNovaWindow,
  targetNavigator: FloatingNovaNavigator | undefined = typeof navigator === "undefined"
    ? undefined
    : navigator as Navigator & FloatingNovaNavigator,
): FloatingNovaSupport {
  if (!targetWindow || !targetNavigator) return { supported: false, reason: "browser_unavailable" }
  if (isMobileClient(targetNavigator))
    return { supported: false, reason: "mobile_not_supported" }
  if (targetWindow.top !== targetWindow) return { supported: false, reason: "top_level_window_required" }
  if (!targetWindow.documentPictureInPicture)
    return { supported: false, reason: "document_picture_in_picture_unavailable" }
  return { supported: true }
}
