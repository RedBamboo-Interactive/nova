import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react"
import { Link } from "react-router-dom"
import { Badge, cn } from "@redbamboo/ui"
import type { ChatBlockProps } from "@redbamboo/chat"
import { JournalChip } from "../journal/journal-entry-card"
import { commitChip, discussionPath, sessionChip, type JournalChipLink } from "../../lib/journal"
import { fetchJournalDay } from "../../lib/journal-api"
import type { JournalDay, JournalEntry } from "../../lib/journal-types"
import { delegationSessionPath } from "../../lib/delegation-session-link"
import type { DiscussionInfo } from "../../lib/types"
import {
  groupLiveReport,
  liveReportAnswer,
  liveReportCounts,
  parseLiveReport,
  splitDiscussionIds,
  type LiveReportRow,
  type LiveReportState,
} from "../../lib/live-report"

/** What the report needs from the Nova around it. */
export interface LiveReportHost {
  discussions: readonly DiscussionInfo[]
  /** The discussion's workstream: its sidebar group. */
  groupOf: (discussionId: string) => { name: string; icon?: string } | null
  /** The Agent whose Journal holds the facts. */
  agent: string | null
  /** Sends an answer as the person's own message in the discussion showing the report. */
  reply?: (text: string) => void
}

const LiveReportHostContext = createContext<LiveReportHost | null>(null)

export function LiveReportHostProvider({ host, children }: { host: LiveReportHost; children: ReactNode }) {
  return <LiveReportHostContext.Provider value={host}>{children}</LiveReportHostContext.Provider>
}

const STATE: Record<LiveReportState, { icon: string; tone: string; label: string; badge: string }> = {
  needs: { icon: "ph-hand-waving", tone: "text-primary", label: "Needs you", badge: "border-transparent bg-primary text-primary-foreground" },
  done: { icon: "ph-check-circle", tone: "text-accent-teal", label: "Done", badge: "border-accent-teal-a50 text-text-secondary dark:text-accent-teal" },
  moving: { icon: "ph-arrows-clockwise", tone: "text-accent-gold", label: "Moving", badge: "border-accent-gold-a30 text-text-secondary dark:text-accent-gold" },
  waiting: { icon: "ph-hourglass-medium", tone: "text-text-muted", label: "Waiting", badge: "border-overlay-12 text-text-muted" },
}

// ── Journal facts ─────────────────────────────────────────────────────────────
// Facts come from the Journal of the report's own day, never from the prose. One read per day is shared by
// every report on screen and kept for a minute.

const FACTS_TTL_MS = 60_000
const factsCache = new Map<string, { at: number; day: Promise<JournalDay | null> }>()

function localDay(at: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0")
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
}

function readDay(agent: string | null, day: string): Promise<JournalDay | null> {
  const key = `${agent ?? ""}|${day}`
  const hit = factsCache.get(key)
  if (hit && Date.now() - hit.at < FACTS_TTL_MS) return hit.day
  const read = fetchJournalDay(agent, day).catch(() => null)
  factsCache.set(key, { at: Date.now(), day: read })
  return read
}

function useJournalThreads(agent: string | null, since: Date | undefined): Map<string, JournalEntry> {
  const day = since ? localDay(since) : null
  const [threads, setThreads] = useState<{ day: string | null; byDiscussion: Map<string, JournalEntry> }>({ day: null, byDiscussion: new Map() })
  useEffect(() => {
    if (!day) return
    let current = true
    void readDay(agent, day).then((journal) => {
      if (!current || !journal) return
      const byDiscussion = new Map<string, JournalEntry>()
      for (const entry of journal.entries) if (entry.kind === "thread" && entry.discussionId) byDiscussion.set(entry.discussionId, entry)
      setThreads({ day, byDiscussion })
    })
    return () => { current = false }
  }, [agent, day])
  return threads.day === day ? threads.byDiscussion : EMPTY_THREADS
}

const EMPTY_THREADS = new Map<string, JournalEntry>()
const MAX_FACTS = 4
const sessionPath = (sessionId: string) => delegationSessionPath({ sessionId })

function factChips(entry: JournalEntry | undefined): JournalChipLink[] {
  if (!entry) return []
  const chips = [
    ...(entry.commits ?? []).map(commitChip),
    ...(entry.sessions ?? []).map(session => sessionChip(session, sessionPath)),
  ]
  if (chips.length <= MAX_FACTS) return chips
  return [...chips.slice(0, MAX_FACTS - 1), { key: "more", label: `${chips.length - MAX_FACTS + 1} more in the Journal`, icon: "ph-notebook", to: null }]
}

// ── Rows ──────────────────────────────────────────────────────────────────────

function DiscussionLink({ id, discussion }: { id: string; discussion: DiscussionInfo | undefined }) {
  if (!discussion) {
    return <span className="min-w-0 truncate font-mono text-[12px] text-text-muted" title="Not among the open discussions">{id}</span>
  }
  const working = discussion.status === "thinking"
  return (
    <Link to={discussionPath(id)} className="group/d inline-flex min-w-0 items-center gap-1.5 text-[13px] font-semibold text-contrast">
      <span aria-hidden="true" className={cn("size-1.5 shrink-0 rounded-full", working ? "bg-accent-gold" : discussion.status === "idle" ? "bg-accent-teal" : "bg-text-disabled")} />
      <span className="min-w-0 truncate group-hover/d:underline">{discussion.title || id}</span>
      {discussion.orchestrated && <i role="img" aria-label="Orchestrated" className="ph-bold ph-heartbeat shrink-0 text-[10px] text-text-muted opacity-60" />}
    </Link>
  )
}

function ReportRow({ row, discussion, entry }: { row: LiveReportRow; discussion: DiscussionInfo | undefined; entry: JournalEntry | undefined }) {
  const s = STATE[row.state]
  const chips = factChips(entry)
  return (
    <li data-slot="live-report-row" data-state={row.state} className="flex gap-2.5 px-3 py-2.5">
      <i aria-hidden="true" className={cn("ph-bold mt-0.5 shrink-0 text-[15px]", s.icon, s.tone)} />
      <div className="flex min-w-0 flex-1 flex-col gap-1.5">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          <DiscussionLink id={row.discussionId} discussion={discussion} />
          <span className="flex-1" />
          <Badge variant="outline" className={cn("shrink-0", s.badge)}>
            {row.state === "waiting" && row.waitingOn ? `Waiting on ${row.waitingOn}` : s.label}
          </Badge>
        </div>
        {row.text && <p className="font-serif text-[14px] leading-snug text-text-primary">{row.text}</p>}
        {chips.length > 0 && (
          <div className="flex flex-wrap gap-1.5">{chips.map(chip => <JournalChip key={chip.key} chip={chip} />)}</div>
        )}
      </div>
    </li>
  )
}

function NeedsRow({ row, discussion, reply }: { row: LiveReportRow; discussion: DiscussionInfo | undefined; reply?: (text: string) => void }) {
  const [chosen, setChosen] = useState<string | null>(null)
  return (
    <div data-slot="live-report-needs" className="flex gap-2.5 border-b border-primary-a30 bg-primary-a10 px-3 py-2.5">
      <i aria-hidden="true" className={cn("ph-bold mt-0.5 shrink-0 text-[15px]", STATE.needs.icon, STATE.needs.tone)} />
      <div className="flex min-w-0 flex-1 flex-col gap-1.5">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          <DiscussionLink id={row.discussionId} discussion={discussion} />
          <span className="flex-1" />
          <Badge variant="outline" className={STATE.needs.badge}>Needs you</Badge>
        </div>
        {row.text && <p className="font-serif text-[14px] leading-snug text-text-primary">{row.text}</p>}
        {row.options && reply && (
          <div className="flex flex-wrap gap-1.5">
            {row.options.map(option => (
              <button
                key={option}
                type="button"
                disabled={chosen !== null}
                aria-pressed={chosen === option}
                onClick={() => {
                  setChosen(option)
                  reply(liveReportAnswer(discussion?.title, row.discussionId, option))
                }}
                className={cn(
                  "min-h-8 rounded-md border px-2.5 text-[12px] transition-colors",
                  chosen === option ? "border-primary bg-primary text-primary-foreground"
                    : "border-primary-a30 bg-background text-contrast hover:bg-primary-a10 disabled:opacity-50",
                )}
              >
                {option}
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

function RepoLine({ name, text, known }: { name: string; text: string; known: (id: string) => DiscussionInfo | undefined }) {
  return (
    <li className="flex min-w-0 gap-2 px-3 py-1.5 text-[12px]">
      <i aria-hidden="true" className="ph-bold ph-git-branch mt-0.5 shrink-0 text-text-muted" />
      <span className="shrink-0 font-mono font-medium text-contrast">{name}</span>
      <span className="min-w-0 text-text-secondary">
        {splitDiscussionIds(text, id => !!known(id)).map((part, index) => part.discussionId
          ? <Link key={index} to={discussionPath(part.discussionId)} title={part.discussionId} className="underline decoration-dotted underline-offset-2 hover:text-contrast">{known(part.discussionId)?.title || part.discussionId}</Link>
          : <span key={index}>{part.text}</span>)}
      </span>
    </li>
  )
}

function timeOf(at: Date): string {
  return at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
}

/** The ```nova-report``` block, rendered. */
export function LiveReportBlock({ source }: ChatBlockProps) {
  const host = useContext(LiveReportHostContext)
  const report = useMemo(() => parseLiveReport(source), [source])
  const discussions = host?.discussions
  const byId = useMemo(() => new Map((discussions ?? []).map(d => [d.id, d])), [discussions])
  const threads = useJournalThreads(host?.agent ?? null, report.sinceAt)
  const groupOf = host?.groupOf ?? (() => null)
  const groups = groupLiveReport(report.rows, groupOf)
  const needs = report.rows.filter(r => r.state === "needs")
  const counts = liveReportCounts(report.rows)
  const summary = (["done", "moving", "waiting"] as const).filter(s => counts[s] > 0).map(s => `${counts[s]} ${s}`).join(" · ")
  const named = groups.some(g => g.name !== null)

  return (
    <div data-slot="live-report" className="overflow-hidden rounded-xl border border-overlay-6 bg-overlay-3">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 border-b border-overlay-6 px-3 py-2">
        <i aria-hidden="true" className="ph-bold ph-clipboard-text text-sm text-primary" />
        <span className="text-[13px] font-semibold text-contrast">{report.title ?? "Report"}</span>
        {report.sinceAt
          ? <span className="text-[12px] text-text-muted">since {timeOf(report.sinceAt)}</span>
          : report.since && <span className="text-[12px] text-text-muted">{report.since}</span>}
        <span className="flex-1" />
        {summary && <span className="text-[11px] text-text-muted">{summary}</span>}
      </div>

      {needs.map(row => <NeedsRow key={`needs:${row.discussionId}:${row.text}`} row={row} discussion={byId.get(row.discussionId)} reply={host?.reply} />)}

      {groups.map(group => (
        <section key={group.name ?? "-"}>
          {named && (
            <div className="flex items-center gap-1.5 px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-text-muted">
              {group.icon && <i aria-hidden="true" className={cn(group.icon, "text-[12px]")} />}
              {group.name ?? "Other"}
            </div>
          )}
          <ul className="divide-y divide-overlay-6">
            {group.rows.map((row, index) => (
              <ReportRow key={`${row.state}:${row.discussionId}:${index}`} row={row} discussion={byId.get(row.discussionId)} entry={threads.get(row.discussionId)} />
            ))}
          </ul>
        </section>
      ))}

      {report.repos.length > 0 && (
        <section className="border-t border-overlay-6">
          <div className="flex items-center gap-1.5 px-3 pb-0.5 pt-2 text-[11px] font-semibold uppercase tracking-wide text-text-muted">
            <i aria-hidden="true" className="ph-bold ph-git-fork text-[12px]" />Checkouts
          </div>
          <ul className="pb-1.5">{report.repos.map(repo => <RepoLine key={repo.name} name={repo.name} text={repo.text} known={id => byId.get(id)} />)}</ul>
        </section>
      )}

      {report.notes.length > 0 && (
        <ul className="border-t border-overlay-6 px-3 py-2 font-serif text-[14px] leading-snug text-text-primary">
          {report.notes.map((note, index) => <li key={index}>{note}</li>)}
        </ul>
      )}
    </div>
  )
}
