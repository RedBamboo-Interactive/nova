import { isBackgroundEvent } from "@redbamboo/chat/background-work"
import type { ChatEvent, ProcessEventResult } from "@redbamboo/chat"
import type { DiscussionInfo } from "./types"

/**
 * What one live session.stream event may do to the discussion's turn state.
 * Background work (a subagent's calls, a task's lifecycle) streams while the
 * main loop is idle: it must never latch streaming, Stop or steering, never
 * force the discussion Active and never invalidate a runtime status read.
 */
export function sessionStreamEffects(
  event: Pick<ChatEvent, "type" | "content" | "parentToolUseId">,
  result: Pick<ProcessEventResult, "isStreaming" | "resumePending" | "background">,
): { applyTurnState: boolean; settleIdle: boolean; forceActive: boolean } {
  if (result.background || isBackgroundEvent(event))
    return { applyTurnState: false, settleIdle: false, forceActive: false }
  // "killed" is terminal but NOT safe/idle yet: the process is being replaced.
  const settleIdle = event.type === "status" && event.content !== "interrupting"
    && !result.isStreaming && !result.resumePending
  return { applyTurnState: true, settleIdle, forceActive: !settleIdle && (result.isStreaming || result.resumePending) }
}

/**
 * A pushed title is only a signal to ask Nova's server for the current
 * RedCompute title. Selection is deliberately absent: inactive ordinary chats
 * must refine too, while standing LIVE/heartbeat titles remain untouched.
 */
export function shouldRequestSessionTitleSync(
  discussion: Pick<DiscussionInfo, "type"> | undefined,
  pushedTitle: string | undefined,
): boolean {

  return discussion?.type === "chat" && !!pushedTitle?.trim()
}
/**
 * Stop reasons RedCompute resumes on its own when a prompt arrives.
 * Mirrors SessionStopReasons.IsAutoResumable in
 * redcompute/src/RedCompute.Core/Sessions/SessionStopReasons.cs
 * (restart-recoverable + IdleTimeout + LegacyIdleTtlExpired). Keep in sync.
 */
const REDCOMPUTE_AUTO_RESUMABLE_STOP_REASONS: ReadonlySet<string> = new Set([
  "maintenance_restart",
  "orphaned_on_restart",
  "idle_timeout",
  "idle_ttl_expired",
])

/** Whether a send needs an explicit resume first: false when RedCompute resumes the session itself. */
export function sendNeedsExplicitResume(sessionStatus: string | undefined, stopReason?: string | null): boolean {
  if (sessionStatus === "Active" || sessionStatus === "Idle" || sessionStatus === "Starting") return false
  if (sessionStatus !== "Stopped" && sessionStatus !== "Error") return true
  return !(stopReason && REDCOMPUTE_AUTO_RESUMABLE_STOP_REASONS.has(stopReason))
}

/**
 * Project RedCompute's provider-neutral session lifecycle onto the smaller
 * discussion lifecycle used by the list indicator.
 */
export function discussionStatusForSession(
  sessionStatus: string,
  _discussionType: DiscussionInfo["type"],
  stopReason?: string,
): DiscussionInfo["status"] | null {
  if (sessionStatus === "Active") return "thinking"
  if (sessionStatus === "Idle" || sessionStatus === "Starting") return "idle"
  if (sessionStatus === "Stopped"
      && (stopReason === "maintenance_restart" || stopReason === "orphaned_on_restart"))
    return "idle"
  if (sessionStatus === "Stopped" || sessionStatus === "Error")
    return "stopped"
  return null
}

export function applySessionStatus(
  discussions: DiscussionInfo[],
  discussionId: string,
  sessionStatus: string,
  stopReason?: string,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId || discussion.status === "archived" || discussion.status === "archiving")
      return discussion
    const status = discussionStatusForSession(sessionStatus, discussion.type, stopReason)
    return status && status !== discussion.status ? { ...discussion, status } : discussion
  })
}

/**
 * Apply a non-active provider status and the activity metadata emitted when a
 * turn settles. Keeping this projection pure prevents the WebSocket handler
 * from accidentally preserving the very `thinking` state it needs to clear.
 */
export function applySettledSessionStatus(
  discussions: DiscussionInfo[],
  discussionId: string,
  sessionStatus: string,
  activityAt: string,
  stopReason?: string,
): DiscussionInfo[] {
  return discussions.map((discussion) => {
    if (discussion.id !== discussionId || discussion.status === "archived" || discussion.status === "archiving")
      return discussion
    const status = discussionStatusForSession(sessionStatus, discussion.type, stopReason)
    if (!status || status === "thinking") return discussion
    return {
      ...discussion,
      status,
      lastActivity: activityAt,
    }
  })
}

/**
 * A newly submitted local turn can be durably delivered before RedCompute's
 * provider lifecycle reaches Active. Ignore a transient non-active status only
 * until this client observes Active. Once Active was observed, Idle is the
 * genuine settlement of that turn even when it completed inside the grace
 * window.
 */
export function preservesRecentStreamingLatch(
  sessionStatus: string,
  isStreaming: boolean,
  lastSendAt: number,
  now: number,
  graceMs: number,
  observedActiveAfterSend = false,
): boolean {
  return isStreaming
    && !observedActiveAfterSend
    && sessionStatus !== "Stopped"
    && sessionStatus !== "Error"
    && now - lastSendAt < graceMs
}
