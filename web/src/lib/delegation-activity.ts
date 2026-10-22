import { orderBackgroundActivity, summarizeBackgroundActivity } from "@redbamboo/chat/background-work"
import type { BackgroundTaskInfo } from "@redbamboo/chat"
import type { DelegationActivitySnapshot, DelegationSessionActivity, DiscussionDelegationActivity } from "./types"

export function delegationSessionLabel(status: DelegationSessionActivity["status"]): string {
  return ({ running: "Running", queued: "Queued", starting: "Starting", waiting_to_resume: "Waiting to resume", blocked: "Blocked", failed: "Failed", unavailable: "Status unavailable" })[status]
}

export function delegationAnimationPaused(activity: DiscussionDelegationActivity): boolean {
  return !activity.available || !activity.sessions.some(session => ["running", "queued", "starting"].includes(session.status))
}

export function delegationActivityLabel(activity: DiscussionDelegationActivity): string {
  const count = activity.ongoingCount
  const noun = count === 1 ? "delegation" : "delegations"
  if (!activity.available) return count > 0 ? `${count} ${noun} · status unavailable` : "Delegation status unavailable"
  const states = new Set(activity.sessions.map(session => session.status))
  const state = states.size === 1 ? activity.sessions[0]?.status : "ongoing"
  const label = state === "waiting_to_resume" ? "waiting to resume" : state ?? "ongoing"
  return `${count} ${noun} ${label}…`
}

/** Background tasks (subagents, shells) the session still reports as running. */
export function runningBackgroundTasks(tasks: readonly BackgroundTaskInfo[] | undefined): BackgroundTaskInfo[] {
  return tasks?.filter(task => !task.status || task.status === "running") ?? []
}

/** What a discussion's one sidebar indicator shows, and why. */
export interface DiscussionActivityIndicator {
  /** The highest-priority work: the session, its agents, other background tasks, its delegations, or nothing. */
  source: "session" | "agents" | "tasks" | "delegations" | "none"
  /** The same text the chat's activity row shows for that work; empty for the session and for nothing. */
  label: string
  /** Kind of the leading background task, which picks its colour. */
  leadKind?: string
  paused: boolean
}

/**
 * The chat activity row's priority, reduced to one indicator: the session
 * itself, then agents, then other background tasks, then delegations.
 */
export function discussionActivityIndicator(status: string, activity: DiscussionDelegationActivity | undefined,
  tasks: readonly BackgroundTaskInfo[] | undefined): DiscussionActivityIndicator {
  if (status === "thinking") return { source: "session", label: "", paused: false }
  const running = orderBackgroundActivity(runningBackgroundTasks(tasks))
  const delegations = activity?.ongoingCount ? activity : undefined
  const summary = summarizeBackgroundActivity(running, delegations ? { label: delegationActivityLabel(delegations) } : null)
  if (!summary) return { source: "none", label: "", paused: true }
  if (summary.source === "host") return { source: "delegations", label: summary.label, paused: delegationAnimationPaused(delegations!) }
  return { source: summary.source, label: summary.label, leadKind: running[0]?.kind, paused: false }
}

/** An unavailable read is not a terminal lifecycle observation. */
export function unavailableDelegationActivity(previous: DelegationActivitySnapshot): DelegationActivitySnapshot {
  return Object.fromEntries(Object.entries(previous).map(([id, activity]) => [id, {
    ...activity,
    available: false,
    sessions: activity.sessions.map(session => ({ ...session,
      lastKnownStatus: session.lastKnownStatus ?? (session.status === "unavailable" ? null : session.status),
      status: "unavailable" as const, available: false })),
  }]))
}

/** Lifecycle frames invalidate the authorized read; payloads never supply activity. */
export function invalidatesDelegationActivity(type: string, data: Record<string, unknown>,
  linkedSessionIds: ReadonlySet<string>, discussionIds: ReadonlySet<string>): boolean {
  if (type === "session.updated" || type === "session.input-queue.updated" || type === "ai-session.changed") {
    const id = type === "session.updated" ? data.id : data.sessionId
    return typeof id === "string" && linkedSessionIds.has(id)
  }
  const discussionId = type === "discussion.rotated" ? data.oldDiscussionId : data.discussionId
  if (typeof discussionId !== "string" || !discussionIds.has(discussionId)) return false
  return type === "discussion.changed" || type === "discussion.cleared" || type === "discussion.rotated"
    || type === "discussion.event" && (data.source === "delegation"
      || typeof data.source === "string" && data.source.startsWith("delegate:"))
}
