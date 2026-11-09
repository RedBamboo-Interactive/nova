import { useEffect, useState } from "react"
import { api } from "../lib/api"
import { readFullAccessEnabled } from "../lib/session-access"

/**
 * Whether Full access is turned on for this install, from RedLeaf's access
 * policy. Read again each time `refresh` changes (the Info modal opening), so
 * turning it on in Settings shows the next time the modal opens. Undefined
 * until read or when the read fails: the modal then leaves Full to
 * RedCompute, which refuses it with its own message.
 */
export function useFullAccessEnabled(refresh: unknown): boolean | undefined {
  const [enabled, setEnabled] = useState<boolean | undefined>(undefined)
  useEffect(() => {
    let cancelled = false
    api.get<unknown>("/api/agent-permissions/policy")
      .then(policy => { if (!cancelled) setEnabled(readFullAccessEnabled(policy)) })
      .catch(() => { /* keep the last known value */ })
    return () => { cancelled = true }
  }, [refresh])
  return enabled
}
