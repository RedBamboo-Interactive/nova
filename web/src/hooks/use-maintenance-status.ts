import { useCallback, useEffect, useRef, useState } from "react"
import { useWsSubscribe } from "@redbamboo/utility"
import { useUiEnvironment } from "@redbamboo/ui"
import { parseMaintenanceStatus, type ChatMaintenanceStatus } from "@redbamboo/chat"
import { api } from "../lib/api"
import { LatestTaskCoordinator } from "../lib/latest-task-coordinator"

/** Operational status never hydrates conversation history or changes its lifecycle. */
export function useMaintenanceStatus() {
  const environment = useUiEnvironment()
  const [status, setStatus] = useState<ChatMaintenanceStatus | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const revision = useRef(0)
  const requests = useRef(new LatestTaskCoordinator<string>())
  const mounted = useRef(false)
  const refresh = useCallback(() => {
    const read = ++revision.current
    void requests.current.run("maintenance", async () => {
      try {
        const next = parseMaintenanceStatus(await api.get<unknown>("/api/apps/nova/maintenance"))
        if (!next) throw new Error("Invalid maintenance status")
        if (!mounted.current || read !== revision.current) return
        setStatus(next)
        setUnavailable(false)
      } catch {
        if (!mounted.current || read !== revision.current) return
        // An unavailable read is not evidence that a known pause ended.
        setUnavailable(true)
      }
    }).catch(() => {})
  }, [])

  useWsSubscribe(event => {
    if (event.type === "upstream.connected" || event.type === "websocket.connected") refresh()
    if (event.type === "upstream.disconnected" || event.type === "websocket.disconnected") {
      ++revision.current
      setUnavailable(true)
    }
  })
  useEffect(() => {
    mounted.current = true
    refresh()
    const foreground = () => { if (environment.document.visibilityState !== "hidden") refresh() }
    environment.window.addEventListener("focus", foreground)
    environment.document.addEventListener("visibilitychange", foreground)
    environment.window.addEventListener("nova:maintenance-refresh", refresh)
    return () => {
      mounted.current = false
      ++revision.current
      environment.window.removeEventListener("focus", foreground)
      environment.document.removeEventListener("visibilitychange", foreground)
      environment.window.removeEventListener("nova:maintenance-refresh", refresh)
    }
  }, [environment.window, environment.document, refresh])
  return { status, unavailable }
}
