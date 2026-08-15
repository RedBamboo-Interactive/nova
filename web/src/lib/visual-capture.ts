import { novaExecution } from "./api.ts"
import type { MonitorCapture, MonitorVisualSource } from "./visual-capture-core.ts"
export { monitorCaptureToContext } from "./visual-capture-core.ts"
export type { MonitorBounds, MonitorCapture, MonitorVisualSource, MonitorWindowMetadata } from "./visual-capture-core.ts"

interface MonitorSourceResponse {
  sources: MonitorVisualSource[]
  limitations: string[]
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const error = await response.json().catch(() => ({ message: response.statusText }))
    throw new Error(error.message ?? error.error ?? response.statusText)
  }
  return response.json() as Promise<T>
}

export async function listMonitorVisualSources(signal?: AbortSignal): Promise<MonitorVisualSource[]> {
  const response = await novaExecution.fetch("/api/ui/visual-sources", {
    credentials: "include",
    signal,
  })
  return (await readJson<MonitorSourceResponse>(response)).sources ?? []
}

export async function captureMonitorVisualSource(sourceId: string): Promise<MonitorCapture> {
  const response = await novaExecution.fetch(`/api/ui/visual-sources/${encodeURIComponent(sourceId)}/capture`, {
    method: "POST",
    credentials: "include",
  })
  return readJson<MonitorCapture>(response)
}
