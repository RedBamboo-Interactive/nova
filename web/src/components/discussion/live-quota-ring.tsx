import { createContext, useContext, useEffect, useMemo, useState, useSyncExternalStore, type ReactNode, type SyntheticEvent } from "react"
import { Popover, PopoverContent, PopoverTrigger } from "@redbamboo/ui"
import { formatProviderUsageWindow, remainingProviderUsage, type ProviderUsageLoader } from "@redbamboo/chat"
import {
  ProviderUsageStore,
  quotaRingLabel,
  quotaToneColor,
  summarizeProviderQuota,
  usageKey,
  type QuotaFormat,
  type UsageProviderRef,
} from "../../lib/provider-usage-store"
import type { DiscussionInfo } from "../../lib/types"

/** One cache for the whole client, so docked and floating chats share readings. */
export const providerUsageStore = new ProviderUsageStore()

const QUOTA_FORMAT: QuotaFormat = {
  window: formatProviderUsageWindow,
  remaining: remainingProviderUsage,
}

export interface LiveQuotaContextValue {
  providers: ReadonlyArray<UsageProviderRef & { name: string }>
  /** The plain usage read; the ring never forces it. */
  load: ProviderUsageLoader
  providerFor: (live: DiscussionInfo) => string | null
}

export const LiveQuotaContext = createContext<LiveQuotaContextValue | null>(null)

const RING_STROKE = 1.75

function stop(event: SyntheticEvent) {
  // The ring sits inside the Live row/tab button: opening the summary must not
  // also select the discussion.
  event.stopPropagation()
}

function QuotaRing({ remainingPercent, color, size }: { remainingPercent: number; color: string; size: number }) {
  const radius = (size - RING_STROKE) / 2
  return (
    <svg
      aria-hidden="true"
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      className="pointer-events-none absolute inset-0 size-full -rotate-90"
    >
      <circle cx={size / 2} cy={size / 2} r={radius} fill="none" stroke="currentColor" strokeWidth={RING_STROKE} className="text-overlay-10" />
      {remainingPercent > 0 && (
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth={RING_STROKE}
          strokeLinecap="round"
          pathLength={100}
          strokeDasharray={`${Math.min(100, remainingPercent)} 100`}
          className="transition-[stroke-dasharray] duration-500"
        />
      )}
    </svg>
  )
}

function QuotaSummaryList({ context, liveKey, version }: { context: LiveQuotaContextValue; liveKey: string | null; version: number }) {
  const rows = useMemo(() => {
    const seen = new Set<string>()
    return context.providers.flatMap(provider => {
      const key = usageKey(provider.slug, context.providers)
      if (seen.has(key)) return []
      seen.add(key)
      const snapshot = providerUsageStore.get(key)
      if (!snapshot) return []
      const windows = snapshot.buckets.flatMap(bucket => bucket.windows.map(window => ({
        id: `${bucket.id}:${window.id}`,
        label: snapshot.buckets.length === 1 || bucket.name === snapshot.displayName
          ? formatProviderUsageWindow(window.windowDurationMinutes)
          : `${bucket.name} · ${formatProviderUsageWindow(window.windowDurationMinutes)}`,
        remaining: remainingProviderUsage(window),
        used: Math.max(0, Math.min(100, window.usedPercent)),
      })))
      return windows.length ? [{ key, name: snapshot.displayName || provider.name, windows }] : []
    })
  }, [context.providers, version])
  const checking = context.providers.some(provider => providerUsageStore.get(usageKey(provider.slug, context.providers)) === undefined)

  return (
    <div className="flex flex-col gap-2.5" data-slot="live-quota-summary">
      <span className="text-xs font-medium text-contrast">Provider quota</span>
      {rows.length === 0 && !checking && (
        <span className="text-xs text-text-muted">No quota data from your providers.</span>
      )}
      {rows.map(row => (
        <div key={row.key} className="flex flex-col gap-1" data-provider-quota={row.key}>
          <div className="flex items-center justify-between gap-2 text-xs">
            <span className="truncate text-contrast">{row.name}</span>
            {row.key === liveKey && (
              <span className="shrink-0 text-[10px] font-medium" style={{ color: "var(--color-status-live)" }}>Live</span>
            )}
          </div>
          {row.windows.map(window => (
            <div key={window.id} data-provider-quota-window={window.id}>
              <div className="flex items-baseline justify-between gap-3 text-[11px]">
                <span className="min-w-0 truncate text-text-muted">{window.label}</span>
                <span className="shrink-0 font-medium text-contrast">{window.remaining}% left</span>
              </div>
              <div className="mt-0.5 h-1 overflow-hidden rounded-full bg-overlay-6">
                <div
                  className="h-full rounded-full"
                  style={{ width: `${Math.min(100, window.remaining)}%`, backgroundColor: quotaToneColor(window.used) }}
                />
              </div>
            </div>
          ))}
        </div>
      ))}
      {checking && (
        <span className="text-[11px] text-text-muted" data-slot="live-quota-checking">Checking other providers…</span>
      )}
      <span className="text-[10px] text-text-muted">Full detail in Session info.</span>
    </div>
  )
}

/**
 * A thin ring around the Live icon showing how much quota its provider has
 * left. Renders the icon alone when the quota is unknown. Readings come from
 * the shared client cache (fed by Session Info); the ring itself only asks,
 * never forced, on mount and when the Live discussion's activity changes.
 */
export function LiveQuotaRing({ discussion, size = 16, children }: { discussion: DiscussionInfo; size?: number; children: ReactNode }) {
  const context = useContext(LiveQuotaContext)
  const version = useSyncExternalStore(providerUsageStore.subscribe, providerUsageStore.getVersion)
  const [open, setOpen] = useState(false)
  const providerId = context?.providerFor(discussion) ?? null
  const key = context && providerId ? usageKey(providerId, context.providers) : null

  useEffect(() => {
    if (key && context) void providerUsageStore.ensure(key, context.load)
  }, [context, key, discussion.status, discussion.messageCount])

  useEffect(() => {
    if (!open || !context) return
    for (const provider of context.providers) void providerUsageStore.ensure(usageKey(provider.slug, context.providers), context.load)
  }, [context, open])

  const summary = useMemo(
    () => summarizeProviderQuota(key ? providerUsageStore.get(key) : null, QUOTA_FORMAT),
    [key, version],
  )

  if (!context || !summary) return <>{children}</>

  const label = quotaRingLabel(summary)
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        openOnHover
        delay={150}
        nativeButton={false}
        render={<span />}
        aria-label={label}
        data-slot="live-quota-ring"
        data-quota-provider={summary.provider}
        data-quota-remaining={summary.remainingPercent}
        className="relative inline-flex shrink-0 cursor-pointer items-center justify-center rounded-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50"
        style={{ width: size, height: size }}
        onClick={stop}
        onPointerDown={stop}
        onMouseDown={stop}
        onKeyDown={stop}
      >
        <QuotaRing remainingPercent={summary.remainingPercent} color={quotaToneColor(summary.usedPercent)} size={size} />
        {children}
      </PopoverTrigger>
      <PopoverContent side="bottom" align="start" className="w-60" onClick={stop}>
        <QuotaSummaryList context={context} liveKey={key} version={version} />
      </PopoverContent>
    </Popover>
  )
}
