import { useEffect, useRef } from "react"

export interface ScreenWakeLockSentinelLike {
  readonly released: boolean
  release(): Promise<void>
  addEventListener(type: "release", listener: () => void, options?: { once?: boolean }): void
}

export type ScreenWakeLockRequest = (type: "screen") => Promise<ScreenWakeLockSentinelLike>

/**
 * Owns one best-effort screen wake lock without allowing an asynchronous grant
 * to outlive the voice session that requested it.
 */
export class ScreenWakeLockLease {
  private generation = 0
  private sentinel: ScreenWakeLockSentinelLike | null = null

  async acquire(request: ScreenWakeLockRequest): Promise<boolean> {
    if (this.sentinel && !this.sentinel.released) return true
    const generation = ++this.generation
    try {
      const sentinel = await request("screen")
      if (generation !== this.generation) {
        if (!sentinel.released) await sentinel.release().catch(() => undefined)
        return false
      }
      this.sentinel = sentinel
      sentinel.addEventListener("release", () => {
        if (this.sentinel === sentinel) this.sentinel = null
      }, { once: true })
      return true
    } catch {
      return false
    }
  }

  async release(): Promise<void> {
    this.generation++
    const sentinel = this.sentinel
    this.sentinel = null
    if (sentinel && !sentinel.released) await sentinel.release().catch(() => undefined)
  }
}

function wakeLockRequest(): ScreenWakeLockRequest | null {
  const wakeLock = navigator.wakeLock
  return wakeLock?.request ? wakeLock.request.bind(wakeLock) : null
}

/** Keep the visible voice surface awake without weakening background cleanup. */
export function useScreenWakeLock(active: boolean) {
  const leaseRef = useRef<ScreenWakeLockLease | null>(null)
  if (!leaseRef.current) leaseRef.current = new ScreenWakeLockLease()

  useEffect(() => {
    const lease = leaseRef.current!
    if (!active) {
      void lease.release()
      return
    }
    if (document.visibilityState !== "visible") return
    const request = wakeLockRequest()
    if (request) void lease.acquire(request)
    return () => { void lease.release() }
  }, [active])
}
