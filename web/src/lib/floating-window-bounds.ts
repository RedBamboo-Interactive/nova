export const FLOATING_WINDOW_BOUNDS_KEY = "nova-floating:bounds"
export const DEFAULT_FLOATING_WINDOW_BOUNDS = { width: 420, height: 700 } as const

export interface FloatingWindowBounds {
  width: number
  height: number
}

export interface FloatingWindowMetrics {
  innerWidth: number
  innerHeight: number
}

export interface BoundsStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

function validDimension(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 52 && value <= 10_000
}

export function readFloatingWindowBounds(storage: BoundsStorage): FloatingWindowBounds {
  try {
    const raw = storage.getItem(FLOATING_WINDOW_BOUNDS_KEY)
    if (!raw) return DEFAULT_FLOATING_WINDOW_BOUNDS
    const candidate = JSON.parse(raw) as Partial<FloatingWindowBounds>
    if (!validDimension(candidate.width) || !validDimension(candidate.height))
      return DEFAULT_FLOATING_WINDOW_BOUNDS
    return { width: Math.round(candidate.width), height: Math.round(candidate.height) }
  } catch {
    return DEFAULT_FLOATING_WINDOW_BOUNDS
  }
}

export function writeFloatingWindowBounds(storage: BoundsStorage, metrics: FloatingWindowMetrics): void {
  if (!validDimension(metrics.innerWidth) || !validDimension(metrics.innerHeight)) return
  storage.setItem(FLOATING_WINDOW_BOUNDS_KEY, JSON.stringify({
    width: Math.round(metrics.innerWidth),
    height: Math.round(metrics.innerHeight),
  }))
}
