import type { DiscussionInfo } from "./types"

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

/**
 * The marks after a discussion's title in the list: each behaviour switched on, except the
 * intrinsic LIVE timeline of LIVE and Heartbeat discussions, which isn't a choice. The heartbeat
 * is armed while the Heartbeat's check is due.
 */
export function behaviourMarks(discussion: DiscussionInfo, agentHasHeartbeat: boolean) {
  const on = effectiveBehaviours(discussion, agentHasHeartbeat)
  return {
    orchestrated: on.orchestrated,
    armed: on.orchestrated && discussion.orchestration?.dueAt != null,
    liveTimeline: on.liveTimeline && !hasIntrinsicBehaviours(discussion),
    confidential: on.confidential,
  }
}
