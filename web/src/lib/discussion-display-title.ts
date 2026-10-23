import type { DiscussionInfo } from "./types"

/** What the display-title rule reads from a discussion or search result. */
export type DisplayTitleSubject = Pick<DiscussionInfo, "id" | "title" | "type" | "createdAt" | "agentId">

export interface DisplayTitleOptions {
  /** Viewer locale; omitted means the browser's. */
  locale?: string | string[]
  /** Viewer time zone; omitted means the browser's. */
  timeZone?: string
  /** Decides whether the year is shown; omitted means the current time. */
  now?: Date
}

export type DiscussionTitleResolver = (discussion: DisplayTitleSubject) => string | null

const LIVE_LABEL = "Live"

/**
 * Title resolver for one listing. An explicit title always wins. An untitled
 * Live is named by its start date in the viewer's locale and time zone, with
 * the year only outside the current year; when the same Agent has several
 * untitled Lives that started on that local day, the start time is added so
 * the entries stay distinguishable. Collisions are judged within `peers`, the
 * discussions the surface has loaded. Any other untitled discussion resolves
 * to null so each surface keeps its own placeholder.
 *
 * Nothing is stored: archived Lives keep `type: "live"`, so every past Live is
 * named on read. The server's DiscussionDisplayTitle applies the same rule
 * with ISO dates in UTC for Agent-facing text.
 */
export function createDiscussionTitleResolver(
  peers: readonly DisplayTitleSubject[],
  options: DisplayTitleOptions = {},
): DiscussionTitleResolver {
  const { locale, timeZone } = options
  const dayFormat = new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" })
  const dateFormat = new Intl.DateTimeFormat(locale, { timeZone, day: "numeric", month: "short" })
  const dateWithYearFormat = new Intl.DateTimeFormat(locale, { timeZone, day: "numeric", month: "short", year: "numeric" })
  const timeFormat = new Intl.DateTimeFormat(locale, { timeZone, timeStyle: "short" })
  const format = (date: Date, withYear: boolean, withTime: boolean) => {
    const label = (withYear ? dateWithYearFormat : dateFormat).format(date)
    return withTime ? `${label}, ${timeFormat.format(date)}` : label
  }

  const liveStart = (discussion: DisplayTitleSubject) => {
    if (discussion.type !== "live" || discussion.title?.trim()) return null
    const date = new Date(discussion.createdAt)
    return Number.isNaN(date.getTime()) ? null : date
  }
  const dayKey = (discussion: DisplayTitleSubject, start: Date) => `${discussion.agentId ?? ""}|${dayFormat.format(start)}`

  const sameDay = new Map<string, Set<string>>()
  for (const peer of peers) {
    const start = liveStart(peer)
    if (!start) continue
    const key = dayKey(peer, start)
    let ids = sameDay.get(key)
    if (!ids) sameDay.set(key, ids = new Set())
    ids.add(peer.id)
  }
  const currentYear = dayFormat.format(options.now ?? new Date()).slice(0, 4)

  return (discussion) => {
    const title = discussion.title?.trim()
    if (title) return title
    if (discussion.type !== "live") return null
    const start = liveStart(discussion)
    if (!start) return LIVE_LABEL
    const day = dayFormat.format(start)
    const ids = sameDay.get(dayKey(discussion, start))
    const collides = !!ids && (ids.size > 1 || !ids.has(discussion.id))
    return `${LIVE_LABEL} · ${format(start, day.slice(0, 4) !== currentYear, collides)}`
  }
}

/** One-off form of {@link createDiscussionTitleResolver}. */
export function discussionDisplayTitle(
  discussion: DisplayTitleSubject,
  peers: readonly DisplayTitleSubject[] = [],
  options: DisplayTitleOptions = {},
): string | null {
  return createDiscussionTitleResolver(peers, options)(discussion)
}
