/** What `GET /api/apps/nova/journal` answers. Absent fields mean the source had nothing to say. */
export interface JournalDay {
  agent: string
  day: string
  today: string
  start: string
  end: string
  timeZone: string
  previousDay: string
  nextDay: string | null
  header?: JournalHeader
  summary: JournalSummary
  counts: JournalCounts
  entries: JournalEntry[]
  partial: boolean
  unavailable: string[]
}

export interface JournalHeader {
  outfit?: JournalOutfit
  mood?: JournalMood
}

export interface JournalOutfit {
  id: string
  name: string
  assetUrl?: string
  reason?: string
  nsfw: boolean
}

export interface JournalMood {
  energy?: string
  vibe?: string
}

export interface JournalSummary {
  live?: { discussionId: string; messages: number }
  discordOutgoing?: number
  background: Array<{ group: string; count: number }>
}

export interface JournalCounts {
  all: number
  own: number
  withYou: number
  scheduled: number
  ticks: number
  failed: number
}

export type JournalKind = "thread" | "tick" | "automation" | "automation-group"
export type JournalCause = "asked" | "own" | "with-you" | "scheduled" | "tick" | "unknown"
export type JournalFilter = "own" | "with-you" | "scheduled" | "ticks" | "failed"

export interface JournalEntry {
  id: string
  kind: JournalKind
  cause: JournalCause
  inferred?: boolean
  at: string
  title: string
  status: string
  text?: string
  textMessageUid?: string
  silent?: boolean
  discussionId?: string
  automationId?: string
  filters: JournalFilter[]
  failedJobs?: number
  request?: JournalRequest
  startedFrom?: JournalStartedFrom
  links?: JournalLink[]
  lines?: JournalGroupLine[]
  sessions?: JournalSession[]
  jobs?: JournalJobRef[]
  artifacts?: JournalArtifact[]
  commits?: JournalCommit[]
}

export interface JournalRequest {
  discussionId: string
  messageUid: string
  quote?: string
  at?: string
}

export interface JournalStartedFrom {
  discussionId: string
  title?: string
  messageUid?: string
  inferred?: boolean
}

export interface JournalLink {
  kind: "discussion" | "message" | "note-sent" | "automation" | "job"
  discussionId?: string
  messageUid?: string
  label?: string
  jobId?: string
  automationId?: string
  inferred?: boolean
}

export interface JournalGroupLine {
  at: string
  title: string
  status: string
  jobId: string
  automationId?: string
  text?: string
}

export interface JournalSession {
  sessionId: string
  repository?: string
  title?: string
  status: string
}

export interface JournalJobRef {
  id: string
  name?: string
  status: string
  capability?: string
}

export interface JournalArtifact {
  key: string
  kind: string
  target: string
  title?: string
  mediaType?: string
  messageUid?: string
  at: string
}

export interface JournalCommit {
  hash: string
  shortHash: string
  message?: string
  repository: string
  url?: string
}

/** What `GET /api/apps/nova/journal/days` answers: only the days with activity. */
export interface JournalMonth {
  agent: string
  month: string
  today: string
  days: JournalCalendarDay[]
  partial: boolean
  unavailable: string[]
}

export interface JournalCalendarDay {
  day: string
  own: boolean
  withYou: boolean
  failed: boolean
}
