import { test } from "node:test"
import assert from "node:assert/strict"
import {
  addJournalDays,
  addMonths,
  calendarWeeks,
  commitChip,
  discussionPath,
  filterJournalEntries,
  firstFew,
  journalBody,
  journalFailedCount,
  journalIcon,
  groupJournalSections,
  isJournalPath,
  jobPath,
  journalBadge,
  journalChipLinks,
  journalChips,
  journalDayLabel,
  journalFoldsDetails,
  journalLanding,
  journalNotice,
  journalPath,
  journalSection,
  journalTime,
  journalTone,
  journalWhen,
  monthLabel,
  parseJournalFilter,
  safeJournalHref,
  shouldRefreshJournal,
} from "./journal.ts"
import type { JournalCounts, JournalEntry, JournalFilter } from "./journal-types.ts"

const ZONE = "Europe/Zurich"

function entry(overrides: Partial<JournalEntry> = {}): JournalEntry {
  return {
    id: "thread:aaaaaaaa",
    kind: "thread",
    cause: "with-you",
    at: "2026-06-10T08:00:00Z",
    title: "A thread",
    status: "idle",
    filters: ["with-you"],
    ...overrides,
  }
}

const automationHref = (id: string) => `/entity/${id}`
const sessionPath = (id: string) => `/session/${id}`

test("today has no date in its address so a saved link stays today", () => {
  assert.equal(journalPath(), "/apps/nova/chat/journal")
  assert.equal(journalPath({ day: "2026-06-10", today: "2026-06-10" }), "/apps/nova/chat/journal")
  assert.equal(journalPath({ day: "2026-06-09", today: "2026-06-10" }), "/apps/nova/chat/journal/2026-06-09")
  assert.equal(
    journalPath({ day: "2026-06-09", today: "2026-06-10", agent: "agent-1", filter: "own" }),
    "/apps/nova/chat/journal/2026-06-09?agent=agent-1&filter=own",
  )
})

test("only the Journal's own route counts as the Journal", () => {
  assert.equal(isJournalPath("/apps/nova/chat/journal"), true)
  assert.equal(isJournalPath("/apps/nova/chat/journal/2026-06-09"), true)
  assert.equal(isJournalPath("/apps/nova/journal/projects/note.md"), false)
  assert.equal(isJournalPath("/apps/nova/chat/journal-of-things"), false)
  assert.equal(isJournalPath("/apps/nova/chat/abcd1234"), false)
})

test("links are escaped and carry the message when there is one", () => {
  assert.equal(discussionPath("abc"), "/apps/nova/chat/abc")
  assert.equal(discussionPath("abc", "u/1 2"), "/apps/nova/chat/abc?message=u%2F1%202")
  assert.equal(jobPath("job-1"), "/apps/compute/jobs?select=job-1")
})

const COUNTS: JournalCounts = { all: 5, own: 2, withYou: 0, scheduled: 3, ticks: 0, failed: 1 }

test("All always shows and every other chip needs something under it", () => {
  assert.deepEqual(journalChips(COUNTS).map((chip) => `${chip.label}:${chip.count}`), [
    "All:5", "On my own:2", "Scheduled:3", "Failed:1",
  ])
  assert.deepEqual(journalChips({ all: 0, own: 0, withYou: 0, scheduled: 0, ticks: 0, failed: 0 }).map((chip) => chip.label), ["All"])
})

test("a filter in the URL is read strictly", () => {
  for (const filter of ["own", "with-you", "scheduled", "ticks", "failed"] satisfies JournalFilter[]) {
    assert.equal(parseJournalFilter(filter), filter)
  }
  assert.equal(parseJournalFilter("asked"), null)
  assert.equal(parseJournalFilter("<script>"), null)
  assert.equal(parseJournalFilter(null), null)
})

test("the filters come from the entry and asked sits under With you", () => {
  const asked = entry({ id: "a", cause: "asked", filters: ["with-you", "failed"] })
  const own = entry({ id: "b", cause: "own", filters: ["own"] })
  assert.deepEqual(filterJournalEntries([asked, own], "with-you").map((item) => item.id), ["a"])
  assert.deepEqual(filterJournalEntries([asked, own], "failed").map((item) => item.id), ["a"])
  assert.deepEqual(filterJournalEntries([asked, own], null).map((item) => item.id), ["a", "b"])
  assert.equal(journalBadge(asked), "You asked")
})

test("times read in the journal's zone wherever the reader is", () => {
  assert.equal(journalTime("2026-06-10T08:05:00Z", ZONE), "10:05")
  assert.equal(journalTime("2026-01-10T08:05:00Z", ZONE), "09:05")
  assert.equal(journalTime("2026-06-10T22:30:00Z", "UTC"), "22:30")
  // A platform zone name the browser can't read falls back rather than throwing.
  assert.doesNotThrow(() => journalTime("2026-06-10T08:05:00Z", "W. Europe Standard Time"))
})

test("section boundaries sit at 07, 11, 13 and 18 and the small hours close the evening", () => {
  assert.deepEqual([4, 6, 7, 10, 11, 12, 13, 17, 18, 23, 0, 3].map(journalSection), [
    "night", "night", "morning", "morning", "midday", "midday", "afternoon", "afternoon", "evening", "evening", "evening", "evening",
  ])
})

test("sections keep the entries' order and appear once each", () => {
  const sections = groupJournalSections([
    entry({ id: "a", at: "2026-06-10T02:30:00Z" }), // 04:30
    entry({ id: "b", at: "2026-06-10T04:59:00Z" }), // 06:59
    entry({ id: "c", at: "2026-06-10T05:00:00Z" }), // 07:00
    entry({ id: "d", at: "2026-06-10T20:00:00Z" }), // 22:00
    entry({ id: "e", at: "2026-06-10T23:30:00Z" }), // 01:30 the next morning
  ], ZONE)
  assert.deepEqual(sections.map((section) => [section.label, section.entries.map((item) => item.id)]), [
    ["Night", ["a", "b"]],
    ["Morning", ["c"]],
    ["Evening", ["d", "e"]],
  ])
})

test("calendar arithmetic crosses month, year and clock changes", () => {
  assert.equal(journalDayLabel("2026-11-03"), "Tuesday 3 November")
  assert.equal(addJournalDays("2026-03-01", -1), "2026-02-28")
  assert.equal(addJournalDays("2026-12-31", 1), "2027-01-01")
  assert.equal(addJournalDays("2026-03-28", 1), "2026-03-29")
  assert.equal(addJournalDays("2026-10-25", 1), "2026-10-26")
  assert.equal(addMonths("2026-12", 1), "2027-01")
  assert.equal(addMonths("2026-01", -1), "2025-12")
  assert.equal(monthLabel("2026-11"), "November 2026")
})

test("a quoted message says when relative to the day showing it", () => {
  assert.equal(journalWhen("2026-06-10T08:05:00Z", "2026-06-10", ZONE), "10:05")
  assert.equal(journalWhen("2026-06-09T17:03:00Z", "2026-06-10", ZONE), "yesterday, 19:03")
  assert.equal(journalWhen("2026-06-02T17:03:00Z", "2026-06-10", ZONE), "2 June, 19:03")
  // 03:30 local on the 10th is still the 9th's journal day.
  assert.equal(journalWhen("2026-06-10T01:30:00Z", "2026-06-09", ZONE), "03:30")
  assert.equal(journalWhen(undefined, "2026-06-10", ZONE), null)
})

test("the calendar is Monday first in whole weeks with future days marked", () => {
  // November 2026 starts on a Sunday and ends on a Monday.
  const weeks = calendarWeeks("2026-11", "2026-11-03", "2026-11-02", [
    { day: "2026-11-02", own: true, withYou: false, failed: true },
  ], [{ day: "2026-10-31", own: false, withYou: true, failed: false }])

  assert.equal(weeks.length, 6)
  assert.ok(weeks.every((week) => week.length === 7))
  assert.equal(weeks[0][0].day, "2026-10-26")
  assert.equal(weeks[0][0].inMonth, false)
  assert.equal(weeks[0][5].marks?.withYou, true)
  assert.equal(weeks[0][6].day, "2026-11-01")
  const second = weeks[1][0]
  assert.equal(second.day, "2026-11-02")
  assert.equal(second.selected, true)
  assert.deepEqual(second.marks, { day: "2026-11-02", own: true, withYou: false, failed: true })
  assert.equal(weeks[1][1].today, true)
  assert.equal(weeks[1][1].future, false)
  assert.equal(weeks[1][2].future, true)
  assert.equal(weeks[5][0].day, "2026-11-30")
  assert.equal(weeks[5][6].day, "2026-12-06")
})

test("a month that starts on a Monday leads with no other month", () => {
  const weeks = calendarWeeks("2026-06", "2026-06-30", null, [])
  assert.equal(weeks[0][0].day, "2026-06-01")
  assert.equal(weeks.length, 5)
})

test("only a failed automation reads as failed, and own work has its own tone", () => {
  assert.equal(journalTone(entry({ cause: "own" })), "own")
  const failedRun = entry({ kind: "automation", cause: "scheduled", status: "failed" })
  assert.equal(journalTone(failedRun), "failed")
  assert.equal(journalBadge(failedRun), "Failed")
  assert.equal(journalIcon(failedRun), "ph-warning")
  assert.equal(journalTone(entry({ cause: "asked" })), "with-you")
  assert.equal(journalTone(entry({ cause: "tick", kind: "tick" })), "tick")
  assert.equal(journalTone(entry({ cause: "unknown" })), "unknown")
  assert.equal(journalBadge(entry({ cause: "unknown" })), "Not recorded")
  assert.equal(journalBadge(entry({ status: "running" })), "In progress")
})

test("a thread with failed jobs keeps its own tone, badge and icon, and says the count in words", () => {
  const thread = entry({ status: "idle", failedJobs: 22, filters: ["with-you", "failed"], jobs: [{ id: "j1", status: "Failed" }] })

  assert.equal(journalTone(thread), "with-you")
  assert.equal(journalBadge(thread), "With you")
  assert.equal(journalIcon(thread), "ph-chats-circle")
  assert.equal(journalFailedCount(thread), "22 jobs failed")
  assert.equal(journalFailedCount(entry({ failedJobs: 1 })), "1 job failed")
  assert.equal(journalFailedCount(entry({ failedJobs: 0 })), null)
  assert.equal(journalFailedCount(entry({ kind: "automation", failedJobs: 2 })), null)
  assert.deepEqual(filterJournalEntries([thread], "failed").map((item) => item.id), [thread.id])
})

test("a thread's failed jobs are listed in its fold, and an automation's stay chips", () => {
  const job = { id: "job-1", status: "Failed", name: "Delegated" }
  const thread = entry({ jobs: [job] })
  assert.equal(journalFoldsDetails(thread), true)
  assert.deepEqual(journalChipLinks(thread, automationHref, sessionPath), [])

  const run = entry({ kind: "automation", status: "failed", jobs: [job] })
  assert.equal(journalFoldsDetails(run), false)
  assert.deepEqual(journalChipLinks(run, automationHref, sessionPath).map((chip) => chip.to), ["/apps/compute/jobs?select=job-1"])
})

test("a tick's body starts after the sentence its title already says", () => {
  const tick = (title: string, text?: string) => entry({ kind: "tick", cause: "tick", title, text })

  assert.equal(journalBody(tick("Quiet morning.", "Quiet morning. Nothing else moved.")), "Nothing else moved.")
  assert.equal(journalBody(tick("Quiet morning.", "  Quiet morning.\n\nSecond paragraph.")), "Second paragraph.")
  assert.equal(journalBody(tick("Quiet morning.", "Quiet morning.")), null)
  assert.equal(journalBody(tick("Stayed silent")), null)
})

test("a tick keeps its whole text when the title was cut or the cut would break Markdown", () => {
  const tick = (title: string, text: string) => entry({ kind: "tick", cause: "tick", title, text })
  const long = "A very long first sentence that the server had to cut short because it ran on"

  assert.equal(journalBody(tick(`${long.slice(0, 40)}…`, long)), long)
  assert.equal(journalBody(tick("Something else.", "Quiet morning. More.")), "Quiet morning. More.")
  assert.equal(journalBody(tick("Ran `a.", "Ran `a. b` here.")), "Ran `a. b` here.")
  assert.equal(journalBody(tick("It **was.", "It **was. done** now.")), "It **was. done** now.")
})

test("only a tick drops its first sentence: every other card's text is whole", () => {
  assert.equal(journalBody(entry({ title: "Quiet morning.", text: "Quiet morning. More." })), "Quiet morning. More.")
  assert.equal(journalBody(entry({ kind: "automation", title: "Quiet morning.", text: "Quiet morning." })), "Quiet morning.")
})

test("every link kind becomes a real location", () => {
  const chips = journalChipLinks(entry({
    kind: "automation",
    textMessageUid: "last-words",
    status: "failed",
    links: [
      { kind: "discussion", discussionId: "aaaaaaaa" },
      { kind: "message", discussionId: "bbbbbbbb", messageUid: "u1", label: "Open tick" },
      { kind: "note-sent", discussionId: "cccccccc", messageUid: "n1", label: "Live", inferred: true },
      { kind: "automation", automationId: "auto-1", label: "Nightly" },
      { kind: "job", jobId: "12345678-aaaa" },
    ],
  }), automationHref, sessionPath)

  assert.deepEqual(chips.map((chip) => chip.to), [
    "/apps/nova/chat/aaaaaaaa?message=last-words",
    "/apps/nova/chat/bbbbbbbb?message=u1",
    "/apps/nova/chat/cccccccc?message=n1",
    "/entity/auto-1",
    "/apps/compute/jobs?select=12345678-aaaa",
  ])
  assert.deepEqual(chips.map((chip) => chip.label), ["Open discussion", "Open tick", "Note to Live", "Nightly", "job 12345678"])
  assert.ok(chips[2].title)
  assert.equal(chips[4].destructive, true)
})

test("a link with no target is no chip", () => {
  assert.deepEqual(journalChipLinks(entry({ links: [{ kind: "job" }, { kind: "discussion" }, { kind: "automation" }] }), automationHref, sessionPath), [])
})

test("a commit links to its remote when it has one and is plain text otherwise", () => {
  const linked = commitChip({ hash: "abc", shortHash: "abc1234", repository: "widgets", url: "https://example.test/commit/abc" })
  assert.equal(linked.href, "https://example.test/commit/abc")
  assert.equal(linked.to, null)
  const plain = commitChip({ hash: "abc", shortHash: "abc1234", repository: "widgets" })
  assert.equal(plain.href, undefined)
  assert.equal(plain.to, null)
})

test("a card links only to this origin's paths and to the web", () => {
  assert.equal(safeJournalHref("/api/apps/nova/file?path=x"), "/api/apps/nova/file?path=x")
  assert.equal(safeJournalHref("https://example.test/a"), "https://example.test/a")
  assert.equal(safeJournalHref("javascript:alert(1)"), undefined)
  assert.equal(safeJournalHref("data:text/html,x"), undefined)
  assert.equal(safeJournalHref("//elsewhere.test/a"), undefined)
  assert.equal(safeJournalHref(undefined), undefined)
  assert.equal(commitChip({ hash: "h", shortHash: "h", repository: "r", url: "javascript:alert(1)" }).href, undefined)
})

test("a few sessions and commits stay chips and many fold into the details", () => {
  const session = { sessionId: "s1", status: "open", repository: "widgets" }
  const commit = { hash: "h", shortHash: "h1", repository: "widgets" }
  const one = entry({ sessions: [session], commits: [commit] })
  assert.equal(journalFoldsDetails(one), false)
  assert.deepEqual(journalChipLinks(one, automationHref, sessionPath).map((chip) => chip.to), ["/session/s1", null])

  const many = entry({ sessions: [session, { ...session, sessionId: "s2" }], commits: [commit] })
  assert.equal(journalFoldsDetails(many), true)
  assert.deepEqual(journalChipLinks(many, automationHref, sessionPath), [])
})

test("the first three then the rest", () => {
  assert.deepEqual(firstFew([1, 2, 3, 4, 5], false), { shown: [1, 2, 3], hidden: 2 })
  assert.deepEqual(firstFew([1, 2, 3, 4, 5], true), { shown: [1, 2, 3, 4, 5], hidden: 0 })
  assert.deepEqual(firstFew([1, 2, 3], false), { shown: [1, 2, 3], hidden: 0 })
})

test("only today refreshes and only while it can be seen", () => {
  assert.equal(shouldRefreshJournal({ day: "2026-06-10", today: "2026-06-10" }, "visible"), true)
  assert.equal(shouldRefreshJournal({ day: "2026-06-10", today: "2026-06-10" }, "hidden"), false)
  assert.equal(shouldRefreshJournal({ day: "2026-06-09", today: "2026-06-10" }, "visible"), false)
})

test("today lands on the latest entry and a past day at its top", () => {
  assert.equal(journalLanding({ day: "2026-06-10", today: "2026-06-10" }), "latest")
  assert.equal(journalLanding({ day: "2026-06-09", today: "2026-06-10" }), "top")
})

test("a short read says so plainly, and an empty day is told apart from it", () => {
  assert.deepEqual(journalNotice({ partial: false, unavailable: [], entries: [1] }), { partial: null, empty: false })
  assert.deepEqual(journalNotice({ partial: true, unavailable: ["jobs", "transcripts"], entries: [] }), {
    partial: "This day may be incomplete: jobs, transcripts couldn't be read.",
    empty: true,
  })
})
