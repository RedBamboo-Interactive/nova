import type { ReactNode } from "react"
import { Switch, cn } from "@redbamboo/ui"
import type { DiscussionInfo } from "../../lib/types"
import { BEHAVIOUR_ICONS, ORCHESTRATION_LABELS, behaviourMarks, orchestrationState } from "../../lib/discussion-behaviours"

const markClass = "text-[10px]"
const quietMark = "text-text-muted opacity-60"
/**
 * Ongoing orchestration wears Nova's magenta: --color-domain-presence is the shared token carrying it (#C74B7A,
 * the "Nova Magenta" colour). Not --primary: in the shell that resolves to the RedLeaf brand.
 */
export const ORCHESTRATION_ONGOING_COLOR = "var(--color-domain-presence)"
const ongoingMark = "text-domain-presence"

/** The Info modal's Orchestrated icon colour: magenta while ongoing, muted once settled, as in the list. */
export function orchestrationAccent(discussion: DiscussionInfo): string {
  return orchestrationState(discussion) === "settled" ? "var(--color-text-muted)" : ORCHESTRATION_ONGOING_COLOR
}

/**
 * The discussion list's quiet marks beside a title (the row's badge slot, so a long title never hides them): the icon of each behaviour switched on, in the
 * Info modal's order. The heartbeat is magenta while its orchestration is ongoing, grey once settled. Intrinsic
 * behaviours (the LIVE timeline of LIVE and Heartbeat discussions) aren't marked: they aren't choices.
 */
export function DiscussionBehaviourMarks({ discussion, agentHasHeartbeat }: { discussion: DiscussionInfo; agentHasHeartbeat: boolean }) {
  const { orchestration, liveTimeline, confidential } = behaviourMarks(discussion, agentHasHeartbeat)
  if (!orchestration && !liveTimeline && !confidential) return null
  return (
    <span data-slot="discussion-behaviour-marks" className="inline-flex shrink-0 items-center gap-1">
      {orchestration && (
        <i
          role="img"
          data-orchestration={orchestration}
          aria-label={ORCHESTRATION_LABELS[orchestration]}
          title={ORCHESTRATION_LABELS[orchestration]}
          className={cn(BEHAVIOUR_ICONS.orchestrated, markClass, orchestration === "ongoing" ? ongoingMark : quietMark)}
        />
      )}
      {liveTimeline && (
        <i role="img" aria-label="LIVE timeline" title="LIVE timeline" className={cn(BEHAVIOUR_ICONS.liveTimeline, markClass, quietMark)} />
      )}
      {confidential && (
        <i role="img" aria-label="Confidential" title="Confidential" className={cn(BEHAVIOUR_ICONS.confidential, markClass, quietMark)} />
      )}
    </span>
  )
}

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

/** The Orchestrated row's status in words: ongoing, or settled until you write and why. */
export function orchestrationStatusText(discussion: DiscussionInfo): string {
  const o = discussion.orchestration
  if (orchestrationState(discussion) === "settled") {
    const why = o?.settledReason === "complete" ? "done"
      : o?.settledReason === "needs_you" ? "needs you"
      : o?.settledNote?.trim() || "the Heartbeat left it to you"
    return `Settled until you write · ${why}`
  }
  const chain = o?.chain ?? 0
  const parts = ["Ongoing", discussion.status === "thinking" ? "working" : "the Heartbeat keeps it moving"]
  if (chain > 0) parts.push(`${chain} ${chain === 1 ? "step" : "steps"} since you last wrote`)
  return parts.join(" · ")
}

/** What an Orchestrated discussion is doing, in words; its row's icon carries the same colour as the list mark. */
export function OrchestrationStatus({ discussion }: { discussion: DiscussionInfo }) {
  const text = orchestrationStatusText(discussion)
  return <span data-orchestration={orchestrationState(discussion)} className="block truncate" title={text}>{text}</span>
}
