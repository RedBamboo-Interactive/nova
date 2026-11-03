import { test } from "node:test"
import assert from "node:assert/strict"
import { findCurrentLive, findLiveHeartbeatPair, resolveLiveSidebarSelection } from "./live-heartbeat.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(id: string, type: DiscussionInfo["type"], agentId: string | null, status: DiscussionInfo["status"] = "idle"): DiscussionInfo {
  return {
    id,
    entityId: id,
    title: null,
    sessionId: null,
    status,
    type,
    createdAt: "2026-08-01T00:00:00.000Z",
    lastActivity: "2026-08-01T00:00:00.000Z",
    messageCount: 0,
    lastReadAt: null,
    agentId,
  }
}

test("pairs Live and Heartbeat for the active agent", () => {
  const agentALive = discussion("a-live", "live", "agent-a")
  const agentAHeartbeat = discussion("a-heartbeat", "heartbeat", "agent-a", "thinking")
  const discussions = [
    discussion("b-live", "live", "agent-b"),
    discussion("b-heartbeat", "heartbeat", "agent-b"),
    agentALive,
    agentAHeartbeat,
  ]

  assert.deepEqual(findLiveHeartbeatPair(discussions, agentALive), {
    live: agentALive,
    heartbeat: agentAHeartbeat,
  })
  assert.deepEqual(findLiveHeartbeatPair(discussions, agentAHeartbeat), {
    live: agentALive,
    heartbeat: agentAHeartbeat,
  })
})

test("a Live with no current Heartbeat still has its views, because the Journal is per Agent", () => {
  const live = discussion("live", "live", "agent-a")
  const archivedHeartbeat = discussion("heartbeat", "heartbeat", "agent-a", "archived")
  const alone = { live, heartbeat: null }

  assert.deepEqual(findLiveHeartbeatPair([live], live), alone)
  assert.deepEqual(findLiveHeartbeatPair([live, archivedHeartbeat], live), alone)
  assert.deepEqual(findLiveHeartbeatPair([live, discussion("chat", "chat", "agent-a")], live), alone)
})

test("another Agent's Heartbeat is not this Live's", () => {
  const live = discussion("live", "live", "agent-a")
  assert.deepEqual(findLiveHeartbeatPair([live, discussion("other", "heartbeat", "agent-b")], live), { live, heartbeat: null })
})

test("a Heartbeat without a current Live keeps its own header", () => {
  const heartbeat = discussion("heartbeat", "heartbeat", "agent-a")
  const pastLive = discussion("past-live", "live", "agent-a", "archived")

  assert.equal(findLiveHeartbeatPair([heartbeat], heartbeat), null)
  assert.equal(findLiveHeartbeatPair([heartbeat, pastLive], heartbeat), null)
  assert.equal(resolveLiveSidebarSelection([heartbeat, pastLive], heartbeat.id), heartbeat.id)
})

test("an ordinary discussion has no Live views", () => {
  const live = discussion("live", "live", "agent-a")
  const chat = discussion("chat", "chat", "agent-a")
  assert.equal(findLiveHeartbeatPair([live, chat], chat), null)
})

test("the Journal resolves to the named Agent's current Live", () => {
  const a = discussion("a-live", "live", "agent-a")
  const b = discussion("b-live", "live", "agent-b")
  const past = discussion("past", "live", "agent-c", "archived")

  assert.equal(findCurrentLive([a, b, past], "agent-b"), b)
  assert.equal(findCurrentLive([a, b, past], null), a)
  assert.equal(findCurrentLive([a, b, past], "agent-c"), null)
  assert.equal(findCurrentLive([], null), null)
})

test("an archived Live or Heartbeat is not presented as the current pair", () => {
  const live = discussion("live", "live", "agent-a")
  const heartbeat = discussion("heartbeat", "heartbeat", "agent-a")
  const pastLive = discussion("past-live", "live", "agent-a", "archived")
  const rotatingLive = discussion("rotating-live", "live", "agent-a", "archiving")
  const pastHeartbeat = discussion("past-heartbeat", "heartbeat", "agent-a", "archived")
  const discussions = [live, heartbeat, pastLive, rotatingLive, pastHeartbeat]

  assert.equal(findLiveHeartbeatPair(discussions, pastLive), null)
  assert.equal(findLiveHeartbeatPair(discussions, rotatingLive), null)
  assert.equal(findLiveHeartbeatPair(discussions, pastHeartbeat), null)
  assert.equal(resolveLiveSidebarSelection(discussions, pastHeartbeat.id), pastHeartbeat.id)
})

test("selects the same agent's Live sidebar row while viewing Heartbeat", () => {
  const agentALive = discussion("a-live", "live", "agent-a")
  const agentAHeartbeat = discussion("a-heartbeat", "heartbeat", "agent-a")
  const agentBLive = discussion("b-live", "live", "agent-b")
  const discussions = [agentBLive, discussion("b-heartbeat", "heartbeat", "agent-b"), agentALive, agentAHeartbeat]

  assert.equal(resolveLiveSidebarSelection(discussions, agentAHeartbeat.id), agentALive.id)
  assert.equal(resolveLiveSidebarSelection(discussions, agentBLive.id), agentBLive.id)
})
