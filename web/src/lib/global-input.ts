export const GLOBAL_INPUT_EVENT_TYPE = "os.global-input"
export const GLOBAL_INPUT_LEASE_ENDPOINT = "/api/ui/global-input/leases"

export type GlobalInputLeaseState = "inactive" | "connecting" | "active" | "unavailable"

export interface GlobalInputEvent {
  key: string
  pressed: boolean
  leaseIds: string[]
}

export interface GlobalInputLease {
  leaseId: string
  feature: string
  key: string
  surfaceId: string
  expiresAt: string
  renewAfterMs: number
  eventType: string
}

export function parseGlobalInputEvent(value: unknown): GlobalInputEvent | null {
  if (!value || typeof value !== "object") return null
  const candidate = value as Partial<GlobalInputEvent>
  if (typeof candidate.key !== "string" || typeof candidate.pressed !== "boolean") return null
  if (!Array.isArray(candidate.leaseIds) || candidate.leaseIds.some(id => typeof id !== "string")) return null
  return { key: candidate.key, pressed: candidate.pressed, leaseIds: candidate.leaseIds }
}
