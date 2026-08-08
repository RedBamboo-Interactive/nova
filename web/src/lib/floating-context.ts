import type { UiSurfaceActionResult } from "@redbamboo/utility"

export const FLOATING_NOVA_CAPTURE_CONTEXT_EVENT = "nova:floating-capture-context"

export interface FloatingNovaCaptureContextRequest {
  respond?: (result: UiSurfaceActionResult) => void
}
