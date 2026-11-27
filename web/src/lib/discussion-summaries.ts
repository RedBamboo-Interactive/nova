import type { DiscussionInfo } from "./types"

export const SUMMARY_SETTINGS_CHANGED = "nova:summary-settings-changed"

export interface DiscussionSummary {
  version: string
  text: {
    status: "waiting" | "needs_you" | "in_progress" | "complete" | "unclear"
    label: string
    overview: string
    nextStep: string | null
    userAction: string | null
  }
  source: "fast" | "agent" | "person"
  updatedAt: string
  sessionId: string
  basisActivity: string
  evidenceMessageUids?: string[] | null
}

export interface DiscussionSummaryView {
  discussionId: string
  enabled: boolean
  override: boolean | null
  version: string
  stale: boolean
  summary: DiscussionSummary | null
}

export const summaryPresentation = {
  waiting: { label: "Waiting", tone: "text-accent-gold", icon: "ph-hourglass-simple" },
  needs_you: { label: "Needs you", tone: "text-primary", icon: "ph-hand" },
  in_progress: { label: "In progress", tone: "text-accent-teal", icon: "ph-activity" },
  complete: { label: "Complete", tone: "text-emerald-400", icon: "ph-check" },
  unclear: { label: "Unclear", tone: "text-text-muted", icon: "ph-question" },
} as const

export function currentDiscussionSummary(discussion: DiscussionInfo | null | undefined, view: DiscussionSummaryView | undefined) {
  const summary = view?.summary
  if (!discussion || discussion.confidential || discussion.status !== "idle" || !view?.enabled || view.stale || !summary
    || summary.sessionId !== discussion.sessionId
    || Date.parse(discussion.lastActivity) > Math.max(Date.parse(summary.basisActivity), Date.parse(summary.updatedAt))) return null
  return summary
}
