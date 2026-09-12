import { useState, useEffect, useMemo, useCallback } from "react"
import { api } from "../lib/api"
import type { EventType } from "../lib/types"
import { resolveEventType } from "../lib/event-type-resolution"

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
    return resolveEventType(source, lookup)
  }, [lookup])

  return { types, resolve }
}
