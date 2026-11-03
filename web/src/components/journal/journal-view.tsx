import { useLayoutEffect, useMemo, useRef } from "react"
import { Link, useLocation, useNavigate, useSearchParams } from "react-router-dom"
import { Button, cn } from "@redbamboo/ui"
import { useJournalDay } from "../../hooks/use-journal"
import {
  COMPUTE_JOBS_PATH,
  addJournalDays,
  discussionPath,
  filterJournalEntries,
  groupJournalSections,
  journalChips,
  journalFilterLabel,
  journalLanding,
  journalNotice,
  journalPath,
  journalTime,
  journalTone,
  parseJournalFilter,
} from "../../lib/journal"
import type { JournalDay, JournalEntry, JournalHeader, JournalSummary } from "../../lib/journal-types"
import { JournalCalendar } from "./journal-calendar"
import { JOURNAL_DOT_TONES, JournalEntryCard, type JournalCardContext } from "./journal-entry-card"

interface JournalViewProps {
  /** The Agent named in the address; none means Nova's. */
  agent: string | null
  /** The day named in the address; none means today. */
  date: string | null
  resolveImageSrc: JournalCardContext["resolveImageSrc"]
}

const SUMMARY_CHIP = "inline-flex items-center gap-1.5 rounded-md bg-overlay-6 px-2 py-1 text-[11px] text-text-muted"

function HeaderCard({ header, summary }: { header?: JournalHeader; summary: JournalSummary }) {
  const outfit = header?.outfit
  const mood = header?.mood
  const moodWords = [mood?.energy, mood?.vibe].filter(Boolean).join(", ")
  const live = summary.live
  const discord = summary.discordOutgoing
  if (!outfit && !moodWords && !live && !discord) return null

  return (
    <div data-slot="journal-header" className="flex items-center gap-3 rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2.5">
      {outfit && (
        <div className="flex size-12 shrink-0 items-center justify-center overflow-hidden rounded-full bg-overlay-6 text-text-muted">
          {/* A flagged outfit keeps its name and stays off a screen that may be read in public. */}
          {outfit.assetUrl && !outfit.nsfw
            ? <img src={outfit.assetUrl} alt="" loading="lazy" decoding="async" className="h-full w-full object-cover" />
            : <i aria-hidden="true" className="ph-bold ph-coat-hanger text-lg" />}
        </div>
      )}
      <div className="min-w-0 flex-1">
        {outfit && <div className="truncate text-[13px] font-semibold text-contrast" title={outfit.reason}>{outfit.name}</div>}
        {moodWords && <div className="truncate text-xs text-text-muted">{moodWords}</div>}
      </div>
      <div className="hidden shrink-0 items-center gap-1.5 md:flex">
        {live && (
          <Link to={discussionPath(live.discussionId)} className={cn(SUMMARY_CHIP, "transition-colors hover:bg-overlay-10 hover:text-contrast")}>
            <i aria-hidden="true" className="ph-bold ph-broadcast text-[12px]" />
            Live · {live.messages} messages
          </Link>
        )}
        {discord != null && (
          <span className={SUMMARY_CHIP}>
            <i aria-hidden="true" className="ph-bold ph-discord-logo text-[12px]" />
            Discord · {discord} outgoing
          </span>
        )}
      </div>
    </div>
  )
}

function Entries({ entries, context }: { entries: JournalEntry[]; context: JournalCardContext }) {
  const sections = useMemo(() => groupJournalSections(entries, context.timeZone), [entries, context.timeZone])
  return (
    <div className="flex flex-col gap-4">
      {sections.map((section) => (
        <section key={section.key} aria-label={section.label} className="flex flex-col gap-2">
          <h3 className="text-[11px] font-semibold uppercase tracking-wide text-text-muted md:pl-[4.75rem]">{section.label}</h3>
          <ol className="flex flex-col gap-3 md:gap-2">
            {section.entries.map((entry) => (
              <li key={entry.id} data-journal-entry className="flex min-w-0 flex-col gap-1 md:flex-row md:gap-2.5">
                <div className="flex shrink-0 items-center gap-2 pt-px md:w-16 md:items-start md:justify-end md:pt-3">
                  <time dateTime={entry.at} className="font-mono text-[11px] leading-4 text-text-muted">{journalTime(entry.at, context.timeZone)}</time>
                  <span aria-hidden="true" className={cn("mt-1 hidden size-2 shrink-0 rounded-full md:block", JOURNAL_DOT_TONES[journalTone(entry)])} />
                </div>
                <JournalEntryCard entry={entry} context={context} />
              </li>
            ))}
          </ol>
        </section>
      ))}
    </div>
  )
}

function Footer({ summary }: { summary: JournalSummary }) {
  return (
    <footer className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-overlay-6 pt-3 text-[11px] text-text-muted">
      {summary.background.length > 0 && (
        <span>In the background: {summary.background.map((group) => `${group.group} ${group.count}`).join(" · ")}</span>
      )}
      <Link to={COMPUTE_JOBS_PATH} className="inline-flex min-h-7 items-center gap-1 underline decoration-dotted underline-offset-2 hover:text-contrast">
        <i aria-hidden="true" className="ph-bold ph-cpu text-[12px]" />
        Open Compute
      </Link>
    </footer>
  )
}

/**
 * The journal of one Agent's day. It reads only from `/api/apps/nova/journal` and writes nothing.
 * Tokens and shapes follow Nova's own panels: `ChatView`'s discussion row for the cards,
 * `WorktreeChips` for the link chips, `MasterDetailLayout`'s `md` break for phone against desktop.
 */
export function JournalView({ agent, date, resolveImageSrc }: JournalViewProps) {
  const { day, error, refresh } = useJournalDay(agent, date)
  const navigate = useNavigate()
  const location = useLocation()
  const [searchParams, setSearchParams] = useSearchParams()
  const filter = parseJournalFilter(searchParams.get("filter"))
  const scroller = useRef<HTMLDivElement>(null)
  const landedOn = useRef<string | null>(null)

  const go = (target: string) => navigate(journalPath({
    day: target,
    today: day?.today,
    agent,
    filter,
  }), { state: location.state })

  const setFilter = (next: string | null) => {
    setSearchParams((params) => {
      if (next) params.set("filter", next)
      else params.delete("filter")
      return params
    }, { replace: true })
    scroller.current?.scrollTo({ top: 0 })
  }

  const entries = useMemo(() => day ? filterJournalEntries(day.entries, filter) : [], [day, filter])

  // Once per day opened: a minute's refresh must not move someone who is reading.
  useLayoutEffect(() => {
    const node = scroller.current
    if (!day || !node || landedOn.current === day.day) return
    landedOn.current = day.day
    if (journalLanding(day) === "top") {
      node.scrollTop = 0
      return
    }
    const rows = node.querySelectorAll<HTMLElement>("[data-journal-entry]")
    const last = rows[rows.length - 1]
    if (last) node.scrollTop += last.getBoundingClientRect().top - node.getBoundingClientRect().top - 8
  }, [day])

  const shownDay = day?.day ?? date
  const today = day?.today ?? null

  return (
    <div data-slot="journal-view" className="flex min-h-0 min-w-0 flex-1 flex-col">
      <div className="mx-auto flex w-full max-w-3xl shrink-0 items-center gap-1.5 px-3 py-2">
        <Button
          variant="outline"
          size="icon"
          aria-label="Previous day"
          disabled={!day && !date}
          onClick={() => { const previous = day?.previousDay ?? (date ? addJournalDays(date, -1) : null); if (previous) go(previous) }}
        >
          <i aria-hidden="true" className="ph-bold ph-caret-left" />
        </Button>
        {shownDay && today ? (
          <JournalCalendar agent={agent} day={shownDay} today={today} onSelect={go} />
        ) : (
          <span className="px-2 text-[13px] text-text-muted">{date ?? "Today"}</span>
        )}
        <Button
          variant="outline"
          size="icon"
          aria-label="Next day"
          disabled={!day?.nextDay}
          onClick={() => { if (day?.nextDay) go(day.nextDay) }}
        >
          <i aria-hidden="true" className="ph-bold ph-caret-right" />
        </Button>
      </div>

      <div ref={scroller} data-slot="journal-scroll" className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden">
        <div className="mx-auto flex w-full max-w-3xl flex-col gap-3 px-3 pb-6">
          {!day && !error && (
            <p role="status" className="py-8 text-center text-sm text-text-muted">Reading the day…</p>
          )}
          {error && (
            <div role="alert" className="flex items-center justify-between gap-3 rounded-lg border border-destructive-a30 bg-destructive-a10 px-3 py-2.5 text-xs">
              <span className="min-w-0">{error}</span>
              <Button variant="outline" size="sm" onClick={() => void refresh()}>Retry</Button>
            </div>
          )}
          {day && <DayBody day={day} entries={entries} filter={filter} setFilter={setFilter} resolveImageSrc={resolveImageSrc} />}
        </div>
      </div>
    </div>
  )
}

function DayBody({ day, entries, filter, setFilter, resolveImageSrc }: {
  day: JournalDay
  entries: JournalEntry[]
  filter: ReturnType<typeof parseJournalFilter>
  setFilter: (next: string | null) => void
  resolveImageSrc: JournalCardContext["resolveImageSrc"]
}) {
  const notice = journalNotice(day)
  const context = useMemo<JournalCardContext>(
    () => ({ day: day.day, timeZone: day.timeZone, resolveImageSrc }),
    [day.day, day.timeZone, resolveImageSrc],
  )

  return (
    <>
      <HeaderCard header={day.header} summary={day.summary} />
      <div role="group" aria-label="Filter entries" className="-mx-3 flex gap-1.5 overflow-x-auto px-3 pb-0.5 [scrollbar-width:none]">
        {journalChips(day.counts).map((chip) => (
          <Button
            key={chip.label}
            variant={filter === chip.filter ? "secondary" : "ghost"}
            size="sm"
            aria-pressed={filter === chip.filter}
            onClick={() => setFilter(chip.filter)}
            className={cn("h-8 shrink-0 rounded-full border border-overlay-8 px-3 text-xs", filter === chip.filter && "border-primary-a50")}
          >
            {chip.icon && <i aria-hidden="true" className={cn("ph-bold text-[12px]", chip.icon)} />}
            {chip.label}
            {chip.filter !== null && <span className="text-text-muted">{chip.count}</span>}
          </Button>
        ))}
      </div>
      {notice.partial && (
        <p role="status" className="flex items-start gap-2 rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2 text-xs text-text-muted">
          <i aria-hidden="true" className="ph-bold ph-warning-circle mt-px shrink-0" />
          {notice.partial}
        </p>
      )}
      {notice.empty ? (
        <p className="py-8 text-center text-sm text-text-muted">Nothing was recorded for this day.</p>
      ) : entries.length === 0 ? (
        <p className="py-8 text-center text-sm text-text-muted">
          Nothing under {filter ? journalFilterLabel(filter) : "this filter"} for this day.
        </p>
      ) : (
        <Entries entries={entries} context={context} />
      )}
      <Footer summary={day.summary} />
    </>
  )
}
