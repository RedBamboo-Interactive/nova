import { createContext, useContext, useEffect, useMemo, useState, useSyncExternalStore, type ReactNode, type SyntheticEvent } from "react"
import { Popover, PopoverContent, PopoverTrigger } from "@redbamboo/ui"
import { formatProviderUsageWindow, remainingProviderUsage, usageToneColor as sharedUsageToneColor, type ProviderUsageLoader } from "@redbamboo/chat"
import {
  ProviderUsageStore,
  quotaRingLabel,
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

// RedLeaf supplies Chat as a shared singleton. During a plugin-only reload the host can
// temporarily predate this export, so keep the same policy locally until the kernel catches up.
const usageToneColor = typeof sharedUsageToneColor === "function"
  ? sharedUsageToneColor
  : (usedPercent: number) => usedPercent < 75
      ? "var(--color-accent-teal)"
      : usedPercent < 90
        ? "var(--color-accent-gold)"
        : "var(--color-accent-red)"

export interface LiveQuotaContextValue {
  providers: ReadonlyArray<UsageProviderRef & { name: string }>
  /** The plain usage read; the ring never forces it. */
  load: ProviderUsageLoader
  providerFor: (live: DiscussionInfo) => string | null
}

export const LiveQuotaContext = createContext<LiveQuotaContextValue | null>(null)

/** Quota border: 2 px wide, 1 px clear of the avatar, following its corner radius. */
const BORDER_WIDTH = 2
const BORDER_OFFSET = 3
const BORDER_TRACK = "rgb(from var(--contrast) r g b / 0.10)"
const BORDER_MASK = "linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0)"

function stop(event: SyntheticEvent) {
  // The border sits inside the Live row button: opening the summary must not
  // also select the discussion.
  event.stopPropagation()
}

/** Same technique as the chat audio player's progress border: a conic fill clockwise from 12 o'clock. */
function quotaBorderBackground(remainingPercent: number, color: string): string {
  const filled = Math.max(0, Math.min(100, remainingPercent))
  return filled > 0 ? `conic-gradient(${color} ${filled}%, ${BORDER_TRACK} ${filled}%)` : BORDER_TRACK
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
                  style={{ width: `${Math.min(100, window.remaining)}%`, backgroundColor: usageToneColor(window.used) }}
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
 * A border around the Live avatar showing how much quota its provider has
 * left. Renders the avatar alone when the quota is unknown. Hovering or
 * clicking the avatar opens every provider's summary. Readings come from the
 * shared client cache (fed by Session Info); the border itself only asks,
 * never forced, on mount and when the Live discussion's activity changes.
 */
export function LiveQuotaBorder({ discussion, children }: { discussion: DiscussionInfo; children: ReactNode }) {
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
        data-slot="live-quota-border"
        data-quota-provider={summary.provider}
        data-quota-remaining={summary.remainingPercent}
        className="relative flex size-full cursor-pointer rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 focus-visible:ring-offset-4 focus-visible:ring-offset-transparent"
        onClick={stop}
        onPointerDown={stop}
        onMouseDown={stop}
        onKeyDown={stop}
      >
        {/* Painted first so the avatar's status badge stays on top of it. */}
        <span
          aria-hidden="true"
          data-slot="live-quota-border-track"
          className="pointer-events-none absolute transition-[background] duration-500"
          style={{
            inset: -BORDER_OFFSET,
            padding: BORDER_WIDTH,
            borderRadius: `calc(var(--radius-lg) + ${BORDER_OFFSET}px)`,
            background: quotaBorderBackground(summary.remainingPercent, usageToneColor(summary.usedPercent)),
            mask: BORDER_MASK,
          }}
        />
        {children}
      </PopoverTrigger>
      <PopoverContent side="bottom" align="start" className="w-60" onClick={stop}>
        <QuotaSummaryList context={context} liveKey={key} version={version} />
      </PopoverContent>
    </Popover>
  )
}
