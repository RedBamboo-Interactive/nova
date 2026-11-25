/**
 * A LIVE report: the ```nova-report``` block an Agent writes when it reports on several discussions at once.
 * One line per item, readable as plain text wherever the block isn't rendered (export, Discord, copy):
 *
 *   title: While you were at German
 *   since: 2026-11-25T09:20:00+01:00
 *   needs 34ca9241: OpenCode only has a Cerebras credential here. | Add an Anthropic key | Keep Nova off OpenCode
 *   done ad7278d0: Hotfix Nightly published: RedLeaf 0.1.56.
 *   moving 8978cdde: WinFsp relaunch reworked after review.
 *   waiting 34ca9241 on the rebuild: Codex MCP fix ready and green.
 *   repo redleaf: cb928f8f coordinates, 8978cdde writes, 2 local commits.
 *
 * `needs` is for what is truly the person's; `waiting … on …` names what anything else waits on. Lines that
 * match nothing are kept as notes, never dropped. While a turn streams the last line can be partial.
 */

export type LiveReportState = "needs" | "done" | "moving" | "waiting"

export interface LiveReportRow {
  state: LiveReportState
  discussionId: string
  text: string
  /** What a `waiting` row waits on. */
  waitingOn?: string
  /** A `needs` row's answers, offered as buttons. */
  options?: string[]
}

export interface LiveReportRepo {
  name: string
  text: string
}

export interface LiveReport {
  title?: string
  /** When the reported stretch began, as written; `sinceAt` when it parses. */
  since?: string
  sinceAt?: Date
  rows: LiveReportRow[]
  repos: LiveReportRepo[]
  notes: string[]
}

export const LIVE_REPORT_LANGUAGE = "nova-report"

const ROW = /^(needs|done|moving|waiting)\s+([0-9a-f]{8})(?:\s+on\s+([^:]+?))?\s*:\s*(.*)$/i
const FIELD = /^(title|since)\s*:\s*(.*)$/i
const REPO = /^repo\s+([\w.@/-]+)\s*:\s*(.*)$/i

export function parseLiveReport(source: string): LiveReport {
  const report: LiveReport = { rows: [], repos: [], notes: [] }
  for (const raw of source.split(/\r?\n/)) {
    const line = raw.trim().replace(/^[-*]\s+/, "")
    if (!line) continue
    const field = FIELD.exec(line)
    if (field) {
      const value = field[2].trim()
      if (field[1].toLowerCase() === "title") report.title = value || undefined
      else {
        report.since = value || undefined
        const at = value ? new Date(value) : null
        report.sinceAt = at && !Number.isNaN(at.getTime()) ? at : undefined
      }
      continue
    }
    const row = ROW.exec(line)
    if (row) {
      const state = row[1].toLowerCase() as LiveReportState
      const parts = row[4].split(/\s+\|\s+/)
      const entry: LiveReportRow = { state, discussionId: row[2].toLowerCase(), text: parts[0].trim() }
      if (state === "waiting" && row[3]) entry.waitingOn = row[3].trim()
      if (state === "needs") {
        const options = parts.slice(1).map(option => option.trim()).filter(Boolean)
        if (options.length > 0) entry.options = options
      }
      report.rows.push(entry)
      continue
    }
    const repo = REPO.exec(line)
    if (repo) {
      report.repos.push({ name: repo[1], text: repo[2].trim() })
      continue
    }
    report.notes.push(line)
  }
  return report
}

const STATE_ORDER: Record<LiveReportState, number> = { needs: 0, moving: 1, waiting: 2, done: 3 }

export interface LiveReportGroup {
  /** The workstream: the discussion's sidebar group, or null when it has none. */
  name: string | null
  icon?: string
  rows: LiveReportRow[]
}

/**
 * The rows under their workstreams, in first-mention order; `needs` rows stay out, pinned above.
 * Inside a workstream the authored order holds.
 */
export function groupLiveReport(
  rows: readonly LiveReportRow[],
  groupOf: (discussionId: string) => { name: string; icon?: string } | null,
): LiveReportGroup[] {
  const groups: LiveReportGroup[] = []
  const byName = new Map<string | null, LiveReportGroup>()
  for (const row of rows) {
    if (row.state === "needs") continue
    const group = groupOf(row.discussionId)
    const name = group?.name ?? null
    let bucket = byName.get(name)
    if (!bucket) {
      bucket = { name, icon: group?.icon, rows: [] }
      byName.set(name, bucket)
      groups.push(bucket)
    }
    bucket.rows.push(row)
  }
  // Ungrouped rows last, so named workstreams lead.
  return [...groups.filter(g => g.name !== null), ...groups.filter(g => g.name === null)]
}

export function liveReportCounts(rows: readonly LiveReportRow[]): Record<LiveReportState, number> {
  const counts: Record<LiveReportState, number> = { needs: 0, done: 0, moving: 0, waiting: 0 }
  for (const row of rows) counts[row.state]++
  return counts
}

export function sortByState(rows: readonly LiveReportRow[]): LiveReportRow[] {
  return [...rows].sort((a, b) => STATE_ORDER[a.state] - STATE_ORDER[b.state])
}

/** The answer a `needs` button sends: the option, tied to the discussion it is about. */
export function liveReportAnswer(title: string | null | undefined, discussionId: string, option: string): string {
  const about = title?.trim() || discussionId
  return `On "${about}": ${option}`
}

/** Eight-hex discussion ids inside free text (repo lines), so they can become links. */
export function splitDiscussionIds(text: string, known: (id: string) => boolean): Array<{ text: string; discussionId?: string }> {
  const parts: Array<{ text: string; discussionId?: string }> = []
  let last = 0
  for (const match of text.matchAll(/\b[0-9a-f]{8}\b/g)) {
    const id = match[0]
    if (!known(id)) continue
    const at = match.index ?? 0
    if (at > last) parts.push({ text: text.slice(last, at) })
    parts.push({ text: id, discussionId: id })
    last = at + id.length
  }
  if (last < text.length) parts.push({ text: text.slice(last) })
  return parts
}
