import type { DiscussionInfo, OrchestrationState } from "./types"

/** Each behaviour's icon, shared by the Info modal's Behaviour list and the discussion list's marks. */
export const BEHAVIOUR_ICONS = {
  orchestrated: "ph-bold ph-heartbeat",
  liveTimeline: "ph-bold ph-broadcast",
  confidential: "ph-bold ph-lock-simple",
} as const

/** LIVE and Heartbeat discussions are what they are: their behaviours are intrinsic, not choices. */
export function hasIntrinsicBehaviours(discussion: DiscussionInfo): boolean {
  return discussion.type === "live" || discussion.type === "heartbeat"
}

/** Agents with an open Heartbeat discussion: only those can pick an Orchestrated discussion up. */
export function agentsWithHeartbeat(discussions: readonly DiscussionInfo[]): ReadonlySet<string | null> {
  const agents = new Set<string | null>()
  for (const d of discussions) {
    if (d.type === "heartbeat" && d.agentId && d.status !== "archived" && d.status !== "archiving") agents.add(d.agentId)
  }
  return agents
}

/** What a discussion's behaviours effectively are, as the Info modal shows them. */
export function effectiveBehaviours(discussion: DiscussionInfo, agentHasHeartbeat: boolean) {
  const confidential = discussion.confidential ?? false
  const intrinsic = hasIntrinsicBehaviours(discussion)
  return {
    // The mark is kept while it can't run (Confidential, or no Heartbeat) and shown off meanwhile.
    orchestrated: !intrinsic && (discussion.orchestrated ?? false) && !confidential && agentHasHeartbeat,
    liveTimeline: discussion.liveTimeline ?? intrinsic,
    confidential,
  }
}

/** Ongoing while it is kept moving; settled once done or waiting on its person, until someone writes. */
export function orchestrationState(discussion: DiscussionInfo): OrchestrationState {
  return discussion.orchestration?.state === "settled" ? "settled" : "ongoing"
}

/** The heartbeat mark's accessible name and tooltip, in each state. */
export const ORCHESTRATION_LABELS: Record<OrchestrationState, string> = {
  ongoing: "Orchestrated · ongoing",
  settled: "Orchestrated · settled until you write",
}

/**
 * Whether the list shows a discussion as alive (full title, unread shown) rather than dimmed. Archived is always
 * dimmed. Stopped is dimmed too, except an Orchestrated discussion whose orchestration is ongoing: its session was
 * only released while idle and the Heartbeat will resume it, so it wears the magenta heart and must not look dead.
 * Same rule as that heart, so the two never contradict. Visual only: actions keep their own status checks.
 */
export function discussionLooksAlive(discussion: DiscussionInfo, agentHasHeartbeat: boolean): boolean {
  if (discussion.status === "archived") return false
  if (discussion.status !== "stopped") return true
  return behaviourMarks(discussion, agentHasHeartbeat).orchestration === "ongoing"
}

/**
 * The marks after a discussion's title in the list: each behaviour switched on, except the
 * intrinsic LIVE timeline of LIVE and Heartbeat discussions, which isn't a choice. The heartbeat
 * says whether its orchestration is ongoing or settled.
 */
export function behaviourMarks(discussion: DiscussionInfo, agentHasHeartbeat: boolean) {
  const on = effectiveBehaviours(discussion, agentHasHeartbeat)
  return {
    orchestrated: on.orchestrated,
    orchestration: on.orchestrated ? orchestrationState(discussion) : null,
    liveTimeline: on.liveTimeline && !hasIntrinsicBehaviours(discussion),
    confidential: on.confidential,
  }
}
