import { useState, useEffect, useMemo, useCallback } from "react"
import { api } from "../lib/api"
import type { EventType } from "../lib/types"

const DEFAULT_EVENT: EventType = {
  key: "default",
  name: "Event",
  icon: "ph-fill ph-radio-button",
  color: null,
  description: null,
}

export function useEventTypes() {
  const [types, setTypes] = useState<EventType[]>([])

  useEffect(() => {
    api.get<EventType[]>("/api/apps/nova/event-types")
      .then(setTypes)
      .catch(() => {})
  }, [])

  const lookup = useMemo(() => {
    const map = new Map<string, EventType>()
    for (const t of types) map.set(t.key, t)
    return map
  }, [types])

  const resolve = useCallback((source: string): EventType => {
    const key = source.replace(/^event:/, "").split(":")[0] ?? ""
    return lookup.get(key) ?? DEFAULT_EVENT
  }, [lookup])

  return { types, resolve }
}
