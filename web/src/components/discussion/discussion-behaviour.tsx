import { useEffect, useState, type ReactNode } from "react"
import { Switch, cn } from "@redbamboo/ui"
import type { DiscussionInfo } from "../../lib/types"

/** One behaviour of a discussion: a row in the Info modal's grouped Behaviour list. */
export interface BehaviourItem {
  key: string
  icon: string
  title: string
  /** Its own accent, shown on the icon while it is on. */
  accent: string
  description: string
  checked: boolean
  pending?: boolean
  /** Why it can't be switched on here; replaces the description and disables the switch. */
  blocked?: string | null
  /** What it is doing right now; replaces the description while it is on. */
  status?: ReactNode
  settingPath?: string
  onChange: (checked: boolean) => void
}

function BehaviourRow({ item, closed }: { item: BehaviourItem; closed: boolean }) {
  const on = item.checked
  // A behaviour already on stays switchable off even when it could not be switched on now.
  const blocked = !on && item.blocked ? item.blocked : null
  return (
    <div data-setting-path={item.settingPath} className="flex items-center gap-3 px-3 py-2.5">
      <span
        aria-hidden="true"
        className="grid size-7 shrink-0 place-items-center rounded-md transition-colors"
        style={{
          background: on ? `color-mix(in oklab, ${item.accent} 18%, transparent)` : "var(--color-overlay-6)",
          color: on ? item.accent : "var(--color-text-muted)",
        }}
      >
        <i className={cn(item.icon, "text-sm")} />
      </span>
      <div className="min-w-0 flex-1">
        <div className="text-xs font-medium text-contrast">{item.title}</div>
        <div className="mt-0.5 truncate text-[11px] text-text-muted" title={blocked ?? item.description}>
          {blocked ?? (on && item.status ? item.status : item.description)}
        </div>
      </div>
      <Switch
        size="sm"
        className="shrink-0"
        checked={on}
        onCheckedChange={item.onChange}
        disabled={item.pending || closed || !!blocked}
        aria-busy={item.pending}
        aria-label={item.title}
      />
    </div>
  )
}

/** The Info modal's Behaviour section: every per-discussion behaviour as one row of one list. */
export function DiscussionBehaviourList({ items, closed }: { items: BehaviourItem[]; closed: boolean }) {
  return (
    <div data-slot="discussion-behaviour" className="divide-y divide-overlay-6 rounded-lg border border-overlay-6 bg-overlay-3">
      {items.map(item => <BehaviourRow key={item.key} item={item} closed={closed} />)}
    </div>
  )
}

/** Re-renders on an interval so a countdown stays honest while the modal is open. */
function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}

function inMinutes(ms: number): string {
  if (ms < 60_000) return "under a minute"
  const minutes = Math.round(ms / 60_000)
  return minutes === 1 ? "1 min" : `${minutes} min`
}

/** What an Orchestrated discussion is doing: armed, working, or handed over, and how far the pacing has gone. */
export function OrchestrationStatus({ discussion }: { discussion: DiscussionInfo }) {
  const now = useNow(15_000)
  const dueAt = discussion.orchestration?.dueAt ? Date.parse(discussion.orchestration.dueAt) : null
  const chain = discussion.orchestration?.chain ?? 0
  const parts: string[] = []
  if (dueAt != null) parts.push(dueAt > now ? `Heartbeat checks in ${inMinutes(dueAt - now)}` : "Heartbeat checking now")
  else if (discussion.status === "thinking") parts.push("Working · Heartbeat checks once it goes idle")
  else if (chain > 0) parts.push("With the Heartbeat")
  else parts.push("Heartbeat checks 3 min after it goes idle")
  if (chain > 0) parts.push(`${chain} ${chain === 1 ? "step" : "steps"} since you last wrote`)
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      {dueAt != null && (
        <span aria-hidden="true" className="relative flex size-1.5 shrink-0">
          <span className="absolute inline-flex size-full animate-ping rounded-full bg-accent-red opacity-60" />
          <span className="relative inline-flex size-1.5 rounded-full bg-accent-red" />
        </span>
      )}
      <span className="truncate">{parts.join(" · ")}</span>
    </span>
  )
}
