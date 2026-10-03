import React from "react"
import { createRoot } from "react-dom/client"
import { WsEventContext } from "@redbamboo/utility"
import { MaintenanceNotice } from "../../../../redbamboo-packages/packages/chat/src/components/maintenance-notice"
import { useMaintenanceStatus } from "../../src/hooks/use-maintenance-status"
import { api } from "../../src/lib/api"
const listeners = new Set<(event: { type: string; data: unknown }) => void>()
const test = { pending: [] as { resolve: (s: unknown) => void; reject: (e: Error) => void }[], reads: 0,
  event: (type: string) => listeners.forEach(f => f({ type, data: {} })),
  refresh: () => window.dispatchEvent(new Event("nova:maintenance-refresh")),
  value: null as ReturnType<typeof useMaintenanceStatus> | null }
Object.assign(api, { get: (path: string) => {
  if (path !== "/api/apps/nova/maintenance") throw new Error("Unexpected read: " + path)
  test.reads++
  return new Promise((resolve, reject) => test.pending.push({ resolve, reject }))
} })
Object.assign(window, { maintenanceTest: test })
const context = { subscribe: (handler: (event: { type: string; data: unknown }) => void) => {
  listeners.add(handler); return () => { listeners.delete(handler) }
}, dispatch: () => {} }
function App() {
  const value = useMaintenanceStatus(); test.value = value
  return <div><MaintenanceNotice status={value.status} unavailable={value.unavailable}
    hasServerSession={!new URLSearchParams(location.search).has("new")} />
    <output>{value.status?.state ?? "unknown"}:{String(value.unavailable)}</output></div>
}
createRoot(document.getElementById("root")!).render(<WsEventContext.Provider value={context}><App /></WsEventContext.Provider>)
