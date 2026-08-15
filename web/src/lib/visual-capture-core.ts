import type { VisibleAppContext } from "@redbamboo/utility"

export interface MonitorBounds {
  left: number
  top: number
  width: number
  height: number
}

export interface MonitorVisualSource {
  id: string
  name: string
  deviceName: string
  primary: boolean
  bounds: MonitorBounds
  previewMediaType: "image/jpeg"
  previewBase64: string
  applications: string[]
}

export interface MonitorWindowMetadata {
  application: string
  title: string
  processId: number
  bounds: MonitorBounds
}

export interface MonitorCapture extends Omit<MonitorVisualSource, "previewMediaType" | "previewBase64"> {
  capturedAt: string
  mediaType: "image/png"
  base64: string
  windows: MonitorWindowMetadata[]
}

export function monitorCaptureToContext(capture: MonitorCapture): VisibleAppContext {
  const applications = capture.applications.filter(Boolean)
  return {
    app: capture.name,
    appId: "desktop-monitor",
    url: `screen://local/${capture.id}`,
    title: capture.primary ? `${capture.name} (Primary)` : capture.name,
    description: applications.length > 0
      ? `${capture.bounds.width}×${capture.bounds.height} · ${applications.join(", ")}`
      : `${capture.bounds.width}×${capture.bounds.height}`,
    screenshot: { mediaType: capture.mediaType, base64: capture.base64 },
    extra: {
      sourceKind: "physical-monitor",
      monitorId: capture.id,
      deviceName: capture.deviceName,
      primary: capture.primary,
      bounds: capture.bounds,
      capturedAt: capture.capturedAt,
      applications,
      visibleWindows: capture.windows,
      visibilitySemantics: "Non-minimized, non-cloaked top-level windows intersecting the captured monitor; occlusion is not inferred.",
    },
  }
}
