import assert from "node:assert/strict"
import test from "node:test"
import { delegationActivityLabel, delegationAnimationPaused, delegationSessionLabel, invalidatesDelegationActivity, unavailableDelegationActivity } from "./delegation-activity.ts"
import type { DiscussionDelegationActivity } from "./types.ts"

function activity(...statuses: Exclude<DiscussionDelegationActivity["sessions"][number]["status"], "unavailable">[]): DiscussionDelegationActivity {
  return { ongoingCount: statuses.length, available: true, unknownCount: 0, linkedSessionIds: statuses.map((_, i) => `session-${i}`), sessions: statuses.map((status, index) => ({
    sessionId: `session-${index}`, title: null, repositoryId: null, repository: null, status, available: true,
  })) }
}

test("count and lifecycle labels distinguish queued work from running and mixed activity", () => {
  assert.equal(delegationActivityLabel(activity("running")), "1 delegation running…")
  assert.equal(delegationActivityLabel(activity("running", "running")), "2 delegations running…")
  assert.equal(delegationActivityLabel(activity("queued")), "1 delegation queued…")
  assert.equal(delegationActivityLabel(activity("starting")), "1 delegation starting…")
  assert.equal(delegationActivityLabel(activity("queued", "running")), "2 delegations ongoing…")
})

test("outage preserves the distinct authoritative sessions and count but stops claiming running", () => {
  const original = { parent: activity("running", "queued"), finished: activity() }
  const unavailable = unavailableDelegationActivity(original)
  assert.equal(unavailable.parent!.ongoingCount, 2)
  assert.equal(unavailable.parent!.available, false)
  assert.deepEqual(unavailable.parent!.sessions.map(session => session.status), ["unavailable", "unavailable"])
  assert.equal(delegationActivityLabel(unavailable.parent!), "2 delegations · status unavailable")
  assert.equal(unavailable.finished!.ongoingCount, 0)
  assert.deepEqual(unavailable.finished!.sessions, [])
  assert.equal(original.parent.sessions[0]!.status, "running")
})

test("waiting recovery and blocked or failed accepted work are distinct from running", () => {
  assert.equal(delegationActivityLabel(activity("waiting_to_resume")), "1 delegation waiting to resume…")
  assert.equal(delegationSessionLabel("blocked"), "Blocked")
  assert.equal(delegationSessionLabel("failed"), "Failed")
  for (const status of ["waiting_to_resume", "blocked", "failed"] as const) {
    const snapshot = activity(status)
    assert.equal(snapshot.ongoingCount, 1)
    assert.equal(delegationAnimationPaused(snapshot), true)
    assert.equal(unavailableDelegationActivity({ parent: snapshot }).parent!.sessions[0]!.lastKnownStatus, status)
  }
  assert.equal(delegationAnimationPaused(activity("blocked", "running")), false)
})

test("only linked session lifecycles and originating discussion changes invalidate activity", () => {
  const linked = new Set(["worker"])
  const discussions = new Set(["parent"])
  for (const type of ["session.updated", "session.input-queue.updated", "ai-session.changed"]) {
    assert.equal(invalidatesDelegationActivity(type, { id: "worker", sessionId: "worker" }, linked, discussions), true)
    assert.equal(invalidatesDelegationActivity(type, { id: "unrelated", sessionId: "unrelated" }, linked, discussions), false)
  }
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "delegation" }, linked, discussions), true)
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "delegate:worker", status: "Idle" }, linked, discussions), true)
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "weather" }, linked, discussions), false)
  assert.equal(invalidatesDelegationActivity("discussion.changed", { discussionId: "other-private" }, linked, discussions), false)
  assert.equal(invalidatesDelegationActivity("session.message", { sessionId: "worker" }, linked, discussions), false)
})

test("the sidebar indicator follows the activity row's priority", async () => {
  const { discussionActivityIndicator, runningBackgroundTasks } = await import("./delegation-activity.ts")
  const tasks = [
    { taskId: "shell", kind: "shell", status: "running", startedAt: "2026-10-22T09:00:01Z" },
    { taskId: "agent", kind: "agent", startedAt: "2026-10-22T09:00:05Z" },
    { taskId: "done", kind: "agent", status: "completed" },
    { taskId: "lost", kind: "agent", status: "lost" },
  ]
  assert.deepEqual(runningBackgroundTasks(tasks).map(task => task.taskId), ["shell", "agent"])
  assert.deepEqual(runningBackgroundTasks(undefined), [])
  // The session working outranks everything behind it.
  assert.deepEqual(discussionActivityIndicator("thinking", activity("running"), tasks), { source: "session", label: "", paused: false })
  // Then agents, then other background tasks, then delegations.
  assert.deepEqual(discussionActivityIndicator("idle", activity("running"), tasks),
    { source: "agents", label: "1 agent working", leadKind: "agent", paused: false })
  assert.deepEqual(discussionActivityIndicator("idle", activity("running"), [tasks[0]]),
    { source: "tasks", label: "1 shell working", leadKind: "shell", paused: false })
  assert.deepEqual(discussionActivityIndicator("idle", activity("running", "queued"), [tasks[2]]),
    { source: "delegations", label: "2 delegations ongoing…", paused: false })
  // Delegations that are only waiting or blocked keep a still indicator.
  assert.equal(discussionActivityIndicator("idle", activity("waiting_to_resume"), undefined).paused, true)
  assert.deepEqual(discussionActivityIndicator("idle", activity(), []), { source: "none", label: "", paused: true })
  assert.equal(discussionActivityIndicator("stopped", undefined, undefined).source, "none")
})
