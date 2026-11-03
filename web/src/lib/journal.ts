import type {
  JournalCalendarDay,
  JournalCommit,
  JournalCounts,
  JournalEntry,
  JournalFilter,
  JournalLink,
  JournalSession,
} from "./journal-types.ts"

// ── Routes ───────────────────────────────────────────────────────────────────

export const JOURNAL_ROUTE = "/apps/nova/chat/journal"

/** A journal day's location. Today has no date of its own, so a link to it stays today tomorrow. */
export function journalPath(options: { day?: string | null; today?: string | null; agent?: string | null; filter?: string | null } = {}): string {
  const path = options.day && options.day !== options.today ? `${JOURNAL_ROUTE}/${options.day}` : JOURNAL_ROUTE
  const query = new URLSearchParams()
  if (options.agent) query.set("agent", options.agent)
  if (options.filter) query.set("filter", options.filter)
  const text = query.toString()
  return text ? `${path}?${text}` : path
}

/** True on the Journal's route, which sits beside a discussion id under chat. */
export function isJournalPath(pathname: string): boolean {
  return pathname === JOURNAL_ROUTE || pathname.startsWith(`${JOURNAL_ROUTE}/`)
}

export function discussionPath(discussionId: string, messageUid?: string | null): string {
  const path = `/apps/nova/chat/${encodeURIComponent(discussionId)}`
  return messageUid ? `${path}?message=${encodeURIComponent(messageUid)}` : path
}

export const COMPUTE_JOBS_PATH = "/apps/compute/jobs"

export function jobPath(jobId: string): string {
  return `${COMPUTE_JOBS_PATH}?select=${encodeURIComponent(jobId)}`
}

// ── Filters ──────────────────────────────────────────────────────────────────

export interface JournalChip {
  filter: JournalFilter | null
  label: string
  icon: string
  count: number
}

const CHIPS: ReadonlyArray<{ filter: JournalFilter; label: string; icon: string; count: keyof JournalCounts }> = [
  { filter: "own", label: "On my own", icon: "ph-sparkle", count: "own" },
  { filter: "with-you", label: "With you", icon: "ph-chats-circle", count: "withYou" },
  { filter: "scheduled", label: "Scheduled", icon: "ph-clock", count: "scheduled" },
  { filter: "ticks", label: "Ticks", icon: "ph-heartbeat", count: "ticks" },
  { filter: "failed", label: "Failed", icon: "ph-warning", count: "failed" },
]

/** All, then every filter with something under it. */
export function journalChips(counts: JournalCounts): JournalChip[] {
  return [
    { filter: null, label: "All", icon: "", count: counts.all },
    ...CHIPS.filter((chip) => counts[chip.count] > 0).map((chip) => ({
      filter: chip.filter,
      label: chip.label,
      icon: chip.icon,
      count: counts[chip.count],
    })),
  ]
}

/** A filter from a URL: anything unknown is no filter at all. */
export function parseJournalFilter(value: string | null | undefined): JournalFilter | null {
  return CHIPS.some((chip) => chip.filter === value) ? (value as JournalFilter) : null
}

export function journalFilterLabel(filter: JournalFilter): string {
  return CHIPS.find((chip) => chip.filter === filter)?.label ?? filter
}

export function filterJournalEntries(entries: readonly JournalEntry[], filter: JournalFilter | null): JournalEntry[] {
  return filter ? entries.filter((entry) => entry.filters.includes(filter)) : [...entries]
}

// ── Time ─────────────────────────────────────────────────────────────────────

function zoneOrLocal(timeZone: string | null | undefined): string | undefined {
  if (!timeZone) return undefined
  try {
    new Intl.DateTimeFormat("en-GB", { timeZone })
    return timeZone
  } catch {
    // A machine zone may carry a platform name the browser doesn't know.
    return undefined
  }
}

function hourAndMinute(at: string, timeZone: string | null | undefined): { hour: number; minute: number } {
  const parts = new Intl.DateTimeFormat("en-GB", {
    timeZone: zoneOrLocal(timeZone),
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
  }).formatToParts(new Date(at))
  const read = (type: string) => Number(parts.find((part) => part.type === type)?.value ?? 0)
  return { hour: read("hour"), minute: read("minute") }
}

/** Wall-clock HH:MM where the journal's day is kept, wherever the reader is. */
export function journalTime(at: string, timeZone: string | null | undefined): string {
  const { hour, minute } = hourAndMinute(at, timeZone)
  return `${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}`
}

export type JournalSectionKey = "night" | "morning" | "midday" | "afternoon" | "evening"

const SECTION_LABELS: Record<JournalSectionKey, string> = {
  night: "Night",
  morning: "Morning",
  midday: "Midday",
  afternoon: "Afternoon",
  evening: "Evening",
}

/**
 * Sections by the clock. A journal day runs to 04:00, so the small hours at its end belong to its
 * evening: calling them Night would put a second Night after the Evening.
 */
export function journalSection(hour: number): JournalSectionKey {
  if (hour < 4) return "evening"
  if (hour < 7) return "night"
  if (hour < 11) return "morning"
  if (hour < 13) return "midday"
  if (hour < 18) return "afternoon"
  return "evening"
}

export interface JournalSection {
  key: JournalSectionKey
  label: string
  entries: JournalEntry[]
}

export function groupJournalSections(entries: readonly JournalEntry[], timeZone: string | null | undefined): JournalSection[] {
  const sections: JournalSection[] = []
  for (const entry of entries) {
    const key = journalSection(hourAndMinute(entry.at, timeZone).hour)
    const current = sections[sections.length - 1]
    if (current?.key === key) current.entries.push(entry)
    else sections.push({ key, label: SECTION_LABELS[key], entries: [entry] })
  }
  return sections
}

const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
const MONTH_NAMES = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"]

function parts(day: string): { year: number; month: number; date: number } {
  const [year, month, date] = day.split("-").map(Number)
  return { year, month, date }
}

/** "Tuesday 3 November". Calendar arithmetic only: a journal day is a label, not an instant. */
export function journalDayLabel(day: string): string {
  const { year, month, date } = parts(day)
  const weekday = new Date(Date.UTC(year, month - 1, date)).getUTCDay()
  return `${DAY_NAMES[weekday]} ${date} ${MONTH_NAMES[month - 1]}`
}

/** A day beside the given one, across month and year ends. */
export function addJournalDays(day: string, count: number): string {
  const { year, month, date } = parts(day)
  return new Date(Date.UTC(year, month - 1, date + count)).toISOString().slice(0, 10)
}

/** Where a quoted message sat relative to the journal day showing it. */
export function journalWhen(at: string | null | undefined, day: string, timeZone: string | null | undefined): string | null {
  if (!at) return null
  const time = journalTime(at, timeZone)
  const shown = dayOfInstant(at, timeZone)
  if (shown === day) return time
  if (shown === addJournalDays(day, -1)) return `yesterday, ${time}`
  const { month, date } = parts(shown)
  return `${date} ${MONTH_NAMES[month - 1]}, ${time}`
}

// The journal day an instant falls on, read the way the server reads it: before 04:00 is yesterday.
function dayOfInstant(at: string, timeZone: string | null | undefined): string {
  const formatted = new Intl.DateTimeFormat("en-CA", {
    timeZone: zoneOrLocal(timeZone),
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(new Date(at))
  return hourAndMinute(at, timeZone).hour < 4 ? addJournalDays(formatted, -1) : formatted
}

// ── The calendar ─────────────────────────────────────────────────────────────

export const CALENDAR_WEEKDAYS = ["M", "T", "W", "T", "F", "S", "S"] as const

export function monthOf(day: string): string {
  return day.slice(0, 7)
}

export function addMonths(month: string, count: number): string {
  const [year, value] = month.split("-").map(Number)
  return new Date(Date.UTC(year, value - 1 + count, 1)).toISOString().slice(0, 7)
}

export function monthLabel(month: string): string {
  const [year, value] = month.split("-").map(Number)
  return `${MONTH_NAMES[value - 1]} ${year}`
}

export interface CalendarCell {
  day: string
  label: number
  inMonth: boolean
  future: boolean
  today: boolean
  selected: boolean
  marks: Pick<JournalCalendarDay, "own" | "withYou" | "failed"> | null
}

/**
 * One month's weeks, Monday first. Only whole weeks: the days around the month are shown when they
 * share a week with it, and only the weeks the month reaches.
 */
export function calendarWeeks(
  month: string,
  today: string,
  selected: string | null,
  days: readonly JournalCalendarDay[],
  neighbours: readonly JournalCalendarDay[] = [],
): CalendarCell[][] {
  const [year, value] = month.split("-").map(Number)
  const first = new Date(Date.UTC(year, value - 1, 1))
  const length = new Date(Date.UTC(year, value, 0)).getUTCDate()
  const lead = (first.getUTCDay() + 6) % 7
  const marks = new Map([...neighbours, ...days].map((day) => [day.day, day]))
  const firstCell = addJournalDays(`${month}-01`, -lead)
  const total = Math.ceil((lead + length) / 7) * 7

  const weeks: CalendarCell[][] = []
  for (let index = 0; index < total; index++) {
    const day = addJournalDays(firstCell, index)
    if (index % 7 === 0) weeks.push([])
    weeks[weeks.length - 1].push({
      day,
      label: parts(day).date,
      inMonth: monthOf(day) === month,
      future: day > today,
      today: day === today,
      selected: day === selected,
      marks: marks.get(day) ?? null,
    })
  }
  return weeks
}

// ── What a card says ─────────────────────────────────────────────────────────

export type JournalTone = "own" | "with-you" | "scheduled" | "tick" | "failed" | "running" | "unknown"

/** Only something scheduled fails as a whole. A thread's failed jobs are counted, not its tone. */
function isFailedAutomation(entry: Pick<JournalEntry, "status" | "kind">): boolean {
  return entry.kind === "automation" && entry.status === "failed"
}

/** A card's tone: a failed automation first, then who began it. Own work carries the primary tint. */
export function journalTone(entry: Pick<JournalEntry, "status" | "cause" | "kind">): JournalTone {
  if (isFailedAutomation(entry)) return "failed"
  switch (entry.cause) {
    case "own": return "own"
    case "with-you":
    case "asked": return "with-you"
    case "scheduled": return "scheduled"
    case "tick": return "tick"
    default: return "unknown"
  }
}

export function journalBadge(entry: Pick<JournalEntry, "status" | "cause" | "kind">): string {
  if (isFailedAutomation(entry)) return "Failed"
  if (entry.status === "running") return "In progress"
  switch (entry.cause) {
    case "own": return "On my own"
    case "with-you": return "With you"
    case "asked": return "You asked"
    case "scheduled": return "Scheduled"
    case "tick": return "Tick"
    default: return "Not recorded"
  }
}

export function journalIcon(entry: Pick<JournalEntry, "status" | "cause" | "kind" | "silent">): string {
  if (isFailedAutomation(entry)) return "ph-warning"
  if (entry.kind === "automation-group") return "ph-clock"
  if (entry.kind === "automation") return "ph-clock"
  if (entry.kind === "tick") return entry.silent ? "ph-moon" : "ph-heartbeat"
  if (entry.cause === "own") return "ph-sparkle"
  if (entry.cause === "unknown") return "ph-question"
  return "ph-chats-circle"
}

/** An address a card may link to: this origin's paths and the web, nothing a record could smuggle in. */
export function safeJournalHref(href: string | null | undefined): string | undefined {
  if (!href) return undefined
  return /^(https?:\/\/|\/(?!\/))/i.test(href) ? href : undefined
}

export function shortId(id: string, length = 8): string {
  return id.slice(0, length)
}

export interface JournalChipLink {
  key: string
  label: string
  icon: string
  /** Internal location, or null for an external one. */
  to: string | null
  /** Full address of an external link. */
  href?: string
  mono?: boolean
  destructive?: boolean
  title?: string
}

function linkChip(link: JournalLink, entry: JournalEntry, automationHref: (id: string) => string): JournalChipLink | null {
  switch (link.kind) {
    case "discussion":
      if (!link.discussionId) return null
      return {
        key: `discussion:${link.discussionId}`,
        label: "Open discussion",
        icon: "ph-chat-circle",
        to: discussionPath(link.discussionId, entry.textMessageUid),
      }
    case "message":
      if (!link.discussionId) return null
      return {
        key: `message:${link.discussionId}:${link.messageUid ?? ""}`,
        label: link.label ?? "Open message",
        icon: entry.kind === "tick" ? "ph-heartbeat" : "ph-chat-circle",
        to: discussionPath(link.discussionId, link.messageUid),
      }
    case "note-sent":
      if (!link.discussionId) return null
      return {
        key: `note:${link.discussionId}:${link.messageUid ?? ""}`,
        label: `Note to ${link.label ?? shortId(link.discussionId)}`,
        icon: "ph-paper-plane-tilt",
        to: discussionPath(link.discussionId, link.messageUid),
        title: link.inferred ? "Read from an older record that doesn't say this outright" : undefined,
      }
    case "automation":
      if (!link.automationId) return null
      return {
        key: `automation:${link.automationId}`,
        label: link.label ?? "Automation",
        icon: "ph-clock",
        to: automationHref(link.automationId),
      }
    case "job":
      if (!link.jobId) return null
      return {
        key: `job:${link.jobId}`,
        label: `job ${shortId(link.jobId)}`,
        icon: "ph-cpu",
        to: jobPath(link.jobId),
        mono: true,
        destructive: isFailedAutomation(entry),
      }
    default:
      return null
  }
}

export function commitChip(commit: JournalCommit): JournalChipLink {
  return {
    key: `commit:${commit.hash}`,
    label: `${commit.shortHash} ${commit.repository}`,
    icon: "ph-git-commit",
    to: null,
    href: safeJournalHref(commit.url),
    mono: true,
    title: commit.message,
  }
}

export function sessionChip(session: JournalSession, sessionPath: (id: string) => string | undefined): JournalChipLink {
  const name = session.repository ?? session.title ?? "Session"
  return {
    key: `session:${session.sessionId}`,
    label: `${name} ${shortId(session.sessionId)}`,
    icon: "ph-code",
    to: sessionPath(session.sessionId) ?? null,
    mono: true,
    title: session.title,
  }
}

/** Many sessions or commits fold into the card's details, as do a thread's failed jobs; a handful stay as chips. */
export function journalFoldsDetails(entry: Pick<JournalEntry, "kind" | "sessions" | "commits" | "jobs">): boolean {
  return (entry.sessions?.length ?? 0) > 1
    || (entry.commits?.length ?? 0) > 3
    || failedJobRefs(entry).length > (entry.kind === "thread" ? 0 : 2)
}

/** What a thread's card states about its failed jobs, in words. Its status stays the discussion's. */
export function journalFailedCount(entry: Pick<JournalEntry, "kind" | "failedJobs">): string | null {
  const count = entry.kind === "thread" ? entry.failedJobs ?? 0 : 0
  if (count <= 0) return null
  return `${count} ${count === 1 ? "job" : "jobs"} failed`
}

function failedJobRefs(entry: Pick<JournalEntry, "jobs">) {
  return (entry.jobs ?? []).filter((job) => job.status === "Failed" || job.status === "TimedOut")
}

export { failedJobRefs as journalFailedJobs }

/** The chips of a card as it stands; what it folds away appears in its details instead. */
export function journalChipLinks(
  entry: JournalEntry,
  automationHref: (id: string) => string,
  sessionPath: (id: string) => string | undefined,
): JournalChipLink[] {
  const chips: JournalChipLink[] = []
  for (const link of entry.links ?? []) {
    const chip = linkChip(link, entry, automationHref)
    if (chip) chips.push(chip)
  }
  if (!journalFoldsDetails(entry)) {
    const linked = new Set((entry.links ?? []).map((link) => link.jobId))
    // A thread with any failed job folds, so these are the failed jobs under an automation.
    for (const job of failedJobRefs(entry)) {
      if (!linked.has(job.id)) {
        chips.push({ key: `job:${job.id}`, label: `job ${shortId(job.id)}`, icon: "ph-cpu", to: jobPath(job.id), mono: true, destructive: true, title: job.name })
      }
    }
    for (const session of entry.sessions ?? []) chips.push(sessionChip(session, sessionPath))
    for (const commit of entry.commits ?? []) chips.push(commitChip(commit))
  }
  return chips
}

/** The first few of a long list, and how many stayed folded. */
export function firstFew<T>(items: readonly T[], showAll: boolean, count = 3): { shown: readonly T[]; hidden: number } {
  return showAll || items.length <= count
    ? { shown: items, hidden: 0 }
    : { shown: items.slice(0, count), hidden: items.length - count }
}

/**
 * A tick's card body. Its title is its first sentence, so the body starts after it, and there is none
 * when nothing remains. A title cut short, or a cut through inline code or emphasis, keeps the whole
 * text: the words on the API stay verbatim and the card never shows half a code span.
 */
export function journalBody(entry: Pick<JournalEntry, "kind" | "title" | "text">): string | null {
  const text = entry.text
  if (!text) return null
  if (entry.kind !== "tick") return text
  const { title } = entry
  const leading = text.trimStart()
  if (title.endsWith("…") || !leading.startsWith(title)) return text
  const unbalanced = (mark: string) => title.split(mark).length % 2 === 0
  if (unbalanced("`") || unbalanced("**")) return text
  const rest = leading.slice(title.length).trimStart()
  return rest || null
}

// ── When to read and where to land ───────────────────────────────────────────

export const JOURNAL_REFRESH_MS = 60_000

/** Past days don't change much: only today keeps refreshing, and only while someone can see it. */
export function shouldRefreshJournal(day: { day: string; today: string } | null, visibility: string): boolean {
  return visibility === "visible" && (day === null || day.day === day.today)
}

/** Opening today lands on the latest entry; a past day opens at its top. */
export function journalLanding(day: { day: string; today: string }): "latest" | "top" {
  return day.day === day.today ? "latest" : "top"
}

/** What the plain line under the day bar says, if anything. */
export function journalNotice(day: { partial: boolean; unavailable: readonly string[]; entries: readonly unknown[] }): { partial: string | null; empty: boolean } {
  const names = day.unavailable.join(", ")
  return {
    partial: day.partial
      ? `This day may be incomplete: ${names || "a source"} couldn't be read.`
      : null,
    empty: day.entries.length === 0,
  }
}
