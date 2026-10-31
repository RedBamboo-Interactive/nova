import type { ProviderUsageLoader, ProviderUsageSnapshot, ProviderUsageWindow } from "@redbamboo/chat"

/**
 * How long a client-side quota reading satisfies the Live ring and the summary
 * popover. Forced reads stay in Session Info; the ring never forces one.
 */
export const PROVIDER_USAGE_FRESHNESS_MS = 60_000

/**
 * A Session Info read this soon after another reader's is answered from that
 * reading. On mount the ring and Session Info ask within moments of each other.
 */
export const PROVIDER_USAGE_SHARED_READ_MS = 5_000

interface UsageEntry {
  /** `null` means "no quota known" (unsupported, cold cache, or a failed read). */
  snapshot: ProviderUsageSnapshot | null
  readAt: number
}

/**
 * One client cache of provider quota snapshots, shared by Session Info, the Live
 * quota ring and its popover. Session Info's own reads are recorded here through
 * {@link recordingUsageLoader}, so the ring rides on the existing cadence instead
 * of adding provider calls; its own reads are single-flight and freshness-gated.
 */
export class ProviderUsageStore {
  private readonly entries = new Map<string, UsageEntry>()
  private readonly inflight = new Map<string, Promise<ProviderUsageSnapshot | null>>()
  private readonly listeners = new Set<() => void>()
  private readonly now: () => number
  private version = 0

  constructor(now: () => number = Date.now) {
    this.now = now
  }

  /** `undefined` when never read; `null` when read and no quota is known. */
  get(key: string): ProviderUsageSnapshot | null | undefined {
    return this.entries.get(key)?.snapshot
  }

  getVersion = (): number => this.version

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  record(key: string, snapshot: ProviderUsageSnapshot | null): void {
    this.entries.set(key, { snapshot, readAt: this.now() })
    this.version++
    for (const listener of this.listeners) listener()
  }

  /** The reading for `key` if it is younger than `maxAgeMs`. */
  fresh(key: string, maxAgeMs: number): UsageEntry | undefined {
    const entry = this.entries.get(key)
    return entry && this.now() - entry.readAt < maxAgeMs ? entry : undefined
  }

  /** Reads `key` (never forced) unless a fresh reading or an identical read already exists. */
  ensure(key: string, load: ProviderUsageLoader, maxAgeMs = PROVIDER_USAGE_FRESHNESS_MS): Promise<void> {
    if (this.fresh(key, maxAgeMs)) return Promise.resolve()
    return this.read(key, () => load(key, false)).then(() => {}, () => {})
  }

  /**
   * One non-forced read per key at a time: a caller arriving while it runs gets
   * the same result. A failure is recorded as unknown and still rejects, so a
   * caller such as Session Info can clear its own view.
   */
  read(key: string, load: () => Promise<ProviderUsageSnapshot | null>): Promise<ProviderUsageSnapshot | null> {
    const pending = this.inflight.get(key)
    if (pending) return pending
    const read = load().then(
      snapshot => { this.record(key, snapshot ?? null); return snapshot ?? null },
      // Quota is optional: a failure shows nothing rather than a false value.
      error => { this.record(key, null); throw error },
    ).finally(() => { this.inflight.delete(key) })
    this.inflight.set(key, read)
    return read
  }
}

/**
 * Wraps a loader so every result it returns also lands in `store`. Ordinary
 * reads share a read of the same key in flight or just taken; forced reads
 * (Session Info opening) always reach the provider, as before.
 */
export function recordingUsageLoader(
  store: ProviderUsageStore,
  load: ProviderUsageLoader,
  keyOf: (provider: string) => string = provider => provider,
): ProviderUsageLoader {
  return async (provider, forceRefresh) => {
    const key = keyOf(provider)
    if (!forceRefresh) {
      const recent = store.fresh(key, PROVIDER_USAGE_SHARED_READ_MS)
      if (recent) return recent.snapshot
      return store.read(key, () => load(provider, false))
    }
    try {
      const snapshot = await load(provider, true)
      store.record(key, snapshot ?? null)
      return snapshot
    } catch (error) {
      store.record(key, null)
      throw error
    }
  }
}

export interface UsageProviderRef {
  slug: string
  backend: string
}

/**
 * Session Info asks with a session's backend id (`claude-code`); a discussion or
 * Agent names a configured slug (`anthropic-direct`). Both reach the same
 * account, so a slug whose backend no other configured provider shares reads
 * under the backend id and the two share one cache entry.
 */
export function usageKey(providerId: string, providers: readonly UsageProviderRef[]): string {
  const configured = providers.find(p => p.slug === providerId)
  if (!configured) return providerId
  const sharesBackend = providers.some(p => p !== configured && p.backend === configured.backend)
  return sharesBackend ? configured.slug : configured.backend
}

/** The provider a Live discussion runs on: its open session first, then its own override, then its Agent. */
export function resolveLiveProvider(
  live: { provider?: string | null },
  agentProvider: string | null | undefined,
  sessionProvider?: string | null,
): string | null {
  return sessionProvider || live.provider || agentProvider || null
}

export interface QuotaFormat {
  window: (minutes: number) => string
  remaining: (window: ProviderUsageWindow) => number
}

export interface QuotaSummary {
  provider: string
  displayName: string
  /** The most constrained window: what is actually left to spend. */
  remainingPercent: number
  usedPercent: number
  windowLabel: string
  bucketName: string
  resetsAt: string | null
}

export function summarizeProviderQuota(
  snapshot: ProviderUsageSnapshot | null | undefined,
  format: QuotaFormat,
): QuotaSummary | null {
  if (!snapshot) return null
  let tightest: { bucket: string; window: ProviderUsageWindow } | null = null
  for (const bucket of snapshot.buckets ?? []) {
    for (const window of bucket.windows ?? []) {
      if (!Number.isFinite(window.usedPercent)) continue
      if (!tightest || window.usedPercent > tightest.window.usedPercent) tightest = { bucket: bucket.name, window }
    }
  }
  if (!tightest) return null
  return {
    provider: snapshot.provider,
    displayName: snapshot.displayName || snapshot.provider,
    remainingPercent: format.remaining(tightest.window),
    usedPercent: Math.max(0, Math.min(100, tightest.window.usedPercent)),
    windowLabel: format.window(tightest.window.windowDurationMinutes),
    bucketName: tightest.bucket,
    resetsAt: tightest.window.resetsAt ?? null,
  }
}

export function quotaRingLabel(summary: QuotaSummary): string {
  return `${summary.displayName} quota: ${summary.remainingPercent}% left (${summary.windowLabel} window)`
}
