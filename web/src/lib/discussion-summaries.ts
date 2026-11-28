import type { DiscussionInfo } from "./types"

export const SUMMARY_SETTINGS_CHANGED = "nova:summary-settings-changed"

export type SummaryStatus = "waiting" | "needs_you" | "in_progress" | "complete" | "unclear"

/**
 * Who the work waits on: the reader ("you"), another Nova discussion ("discussion:<id>"),
 * someone outside Nova ("external"), or nobody known (null). Older records omit it.
 */
export type SummaryWaitingOn = "you" | "external" | `discussion:${string}` | null

export interface DiscussionSummary {
  version: string
  text: {
    status: SummaryStatus
    label: string
    overview: string
    nextStep: string | null
    userAction: string | null
    waitingOn?: SummaryWaitingOn
  }
  source: "fast" | "agent" | "person"
  updatedAt: string
  sessionId: string
  basisActivity: string
  evidenceMessageUids?: string[] | null
}

/**
 * Present when the server resolved this discussion's wait through another discussion of the same
 * owner whose own summary needs the reader: the summary then reads needs_you with that action.
 */
export interface DiscussionSummaryVia {
  discussionId: string
  title: string | null
  label: string
  userAction: string
  updatedAt: string
}

export interface DiscussionSummaryView {
  discussionId: string
  enabled: boolean
  override: boolean | null
  version: string
  stale: boolean
  summary: DiscussionSummary | null
  via?: DiscussionSummaryVia | null
}

/**
 * Only "Needs you" asks for attention. Complete is calm, everything else recedes.
 * `tone` colours the glyph; `chip` styles the in-discussion status chip.
 */
export const summaryPresentation = {
  needs_you: {
    label: "Needs you", icon: "ph-fill ph-hand-palm", tone: "text-primary", attention: true,
    chip: "border-primary-a30 bg-primary-a10 text-primary font-semibold",
  },
  complete: {
    label: "Complete", icon: "ph-bold ph-check-circle", tone: "text-accent-teal", attention: false,
    chip: "border-overlay-6 bg-overlay-3 text-accent-teal",
  },
  waiting: {
    label: "Waiting", icon: "ph-bold ph-hourglass-simple", tone: "text-text-muted", attention: false,
    chip: "border-overlay-6 bg-overlay-3 text-text-muted",
  },
  in_progress: {
    label: "In progress", icon: "ph-bold ph-arrow-right", tone: "text-text-muted", attention: false,
    chip: "border-overlay-6 bg-overlay-3 text-text-muted",
  },
  unclear: {
    label: "Unclear", icon: "ph-bold ph-question", tone: "text-text-disabled", attention: false,
    chip: "border-overlay-6 bg-overlay-3 text-text-disabled",
  },
} as const satisfies Record<SummaryStatus, { label: string; icon: string; tone: string; attention: boolean; chip: string }>

export function currentDiscussionSummary(discussion: DiscussionInfo | null | undefined, view: DiscussionSummaryView | undefined) {
  const summary = view?.summary
  if (!discussion || discussion.confidential || discussion.status !== "idle" || !view?.enabled || view.stale || !summary
    || summary.sessionId !== discussion.sessionId
    || Date.parse(discussion.lastActivity) > Math.max(Date.parse(summary.basisActivity), Date.parse(summary.updatedAt))) return null
  return summary
}

/** The discussion a summary waits on, when it names one. */
export function waitingDiscussionId(summary: DiscussionSummary): string | null {
  const value = summary.text.waitingOn
  return typeof value === "string" && value.startsWith("discussion:") && value.length > "discussion:".length
    ? value.slice("discussion:".length) : null
}

export interface SummaryDetails {
  status: SummaryStatus
  statusLabel: string
  overview: string
  nextStep: string | null
  /** Only when the reader has something to do. */
  userAction: string | null
  /** Where that action lives when it belongs to another discussion. */
  actionIn: { discussionId: string; title: string } | null
  /** Who else the work waits on when it is not the reader. */
  waitingOnLabel: string | null
  updatedAt: string
}

/** What the status modal shows, in order. Nothing empty is shown. */
export function summaryDetails(summary: DiscussionSummary, view?: DiscussionSummaryView | null,
  titleOf?: (discussionId: string) => string | null | undefined): SummaryDetails {
  const { text } = summary
  const via = view?.via && text.status === "needs_you" ? view.via : null
  const waitingOn = waitingDiscussionId(summary)
  const title = (id: string, fallback?: string | null) => titleOf?.(id) || fallback || "another discussion"
  return {
    status: text.status,
    statusLabel: summaryPresentation[text.status].label,
    overview: text.overview,
    nextStep: text.nextStep,
    userAction: text.status === "needs_you" ? text.userAction : null,
    actionIn: via ? { discussionId: via.discussionId, title: title(via.discussionId, via.title) } : null,
    waitingOnLabel: text.status !== "waiting" ? null
      : waitingOn ? title(waitingOn)
      : text.waitingOn === "external" ? "Someone outside Nova" : null,
    updatedAt: summary.updatedAt,
  }
}
