import { test } from "node:test"
import assert from "node:assert/strict"
import { appendEvent, orderMessages } from "./message-order.ts"
import type { ChatEvent, MessageBlock } from "@redbamboo/chat"
import { processStreamEvent } from "../../../../redbamboo-packages/packages/chat/src/lib/process-stream-event.ts"

// Frieze ordering has regressed four times, each fix trading one wrong
// placement for another. These fixtures pin the rule down: events render in
// timestamp order, consecutive ones merged into a single block, and the live
// stream must agree with the reloaded transcript on every one of them.

const at = (minute: number) => `2026-07-25T19:${String(minute).padStart(2, "0")}:00.000Z`

let seq = 0
const user = (minute: number, text: string): MessageBlock =>
  ({ id: `u${seq++}`, role: "user", parts: [{ type: "text", content: text }], timestamp: at(minute) })

const nova = (minute: number, text: string): MessageBlock =>
  ({ id: `a${seq++}`, role: "assistant", parts: [{ type: "text", content: text }], timestamp: at(minute) })

/** An event as persisted by the Nova API: text body plus a source tag. */
const event = (minute: number, text: string, source = "event:weather"): MessageBlock =>
  ({ id: `e${seq++}`, role: "user", parts: [{ type: "text", content: text }], timestamp: at(minute), metadata: { source } })

/** Render a block list as a compact shape string: NOVA(a1) [w1+w2] USER(q1). */
function shape(blocks: MessageBlock[]): string {
  return blocks.map((b) => {
    const events = b.parts.filter((p) => p.toolName?.startsWith("event:"))
    if (events.length === b.parts.length && events.length > 0) {
      return `[${events.map((p) => p.content).join("+")}]`
    }
    return `${b.role === "user" ? "USER" : "NOVA"}(${b.parts[0]!.content})`
  }).join(" ")
}

test("consecutive events merge into one block", () => {
  const out = orderMessages([user(4, "q1"), nova(5, "a1"), event(6, "w1"), event(7, "w2"), event(8, "w3")])
  assert.equal(shape(out), "USER(q1) NOVA(a1) [w1+w2+w3]")
})

test("placement does not depend on what comes later in the discussion", () => {
  // The regression that started this: the same events rendered below the reply
  // they followed, then above it as soon as any further turn existed.
  const turn1 = [user(4, "q1"), nova(5, "a1"), event(6, "w1"), event(7, "w2")]
  const turn2 = [...turn1, user(8, "q2"), nova(9, "a2")]

  assert.equal(shape(orderMessages(turn1)), "USER(q1) NOVA(a1) [w1+w2]")
  assert.equal(shape(orderMessages(turn2)), "USER(q1) NOVA(a1) [w1+w2] USER(q2) NOVA(a2)")
})

test("every turn places its events on the same side", () => {
  const out = orderMessages([user(4, "q1"), nova(5, "a1"), event(6, "w1"), user(8, "q2"), nova(9, "a2"), event(10, "w2")])
  assert.equal(shape(out), "USER(q1) NOVA(a1) [w1] USER(q2) NOVA(a2) [w2]")
})

test("events never sail past an intervening ambient post", () => {
  // The LIVE timeline: heartbeat posts interleaved with events. w2 arrived
  // between the two posts and belongs between them.
  const out = orderMessages([event(1, "w1"), event(2, "w2"), nova(3, "beat1"), event(4, "w3"), nova(5, "beat2"), event(6, "w4")])
  assert.equal(shape(out), "[w1+w2] NOVA(beat1) [w3] NOVA(beat2) [w4]")
})

test("input order does not matter, timestamps do", () => {
  const blocks = [event(7, "w2"), nova(5, "a1"), event(6, "w1"), user(4, "q1")]
  assert.equal(shape(orderMessages(blocks)), "USER(q1) NOVA(a1) [w1+w2]")
})

test("events arriving before any conversation lead the transcript", () => {
  assert.equal(shape(orderMessages([event(1, "w0"), user(4, "q1"), nova(5, "a1")])), "[w0] USER(q1) NOVA(a1)")
})

test("legacy events carry no source tag and are found by their wrapper", () => {
  const legacy: MessageBlock = {
    id: "legacy", role: "user", timestamp: at(6),
    parts: [{ type: "text", content: `<nova-event source="weather" type="generic">Overcast</nova-event>` }],
  }
  const out = orderMessages([user(4, "q1"), nova(5, "a1"), legacy])
  assert.equal(shape(out), "USER(q1) NOVA(a1) [Overcast]")
})

test("event parts carry their own timestamp and structured payload", () => {
  const withData: MessageBlock = {
    id: "d", role: "user", timestamp: at(6),
    parts: [{ type: "text", content: "Overcast" }],
    metadata: { source: "event:weather", eventData: { temp: 18, condition: "Overcast" } },
  }
  const [group] = orderMessages([withData])
  const payload = JSON.parse(group!.parts[0]!.toolInput!)
  // Merged groups keep only the first event's block timestamp, so each part
  // needs its own copy for the detail modal.
  assert.equal(payload.timestamp, at(6))
  assert.deepEqual(payload.data, { temp: 18, condition: "Overcast" })
})

test("an event type resolver supplies the frieze icon and colour", () => {
  const resolve = (source: string) =>
    source === "event:weather" ? { key: "weather", name: "Weather", icon: "ph-cloud", color: "#abc", description: null } : undefined
  const [group] = orderMessages([event(6, "w1")], resolve)
  const payload = JSON.parse(group!.parts[0]!.toolInput!)
  assert.equal(payload.icon, "ph-cloud")
  assert.equal(payload.color, "#abc")
})

test("merged groups keep unique block ids", () => {
  // Same millisecond, distinct blocks: a duplicate React key drops one.
  const sameMs = [event(6, "w1"), nova(7, "a1"), event(8, "w2")]
  const out = orderMessages(sameMs.map((b) => ({ ...b, timestamp: at(6) })))
  const ids = out.map((b) => b.id)
  assert.equal(new Set(ids).size, ids.length, `duplicate ids: ${ids.join(", ")}`)
})

// ── live stream agrees with the reloaded transcript ───────────────────

/** Replay a discussion event-by-event the way the socket handler does. */
function replayLive(blocks: MessageBlock[]): MessageBlock[] {
  let live: MessageBlock[] = []
  for (const block of blocks) {
    const source = block.metadata?.source
    if (typeof source === "string" && source.startsWith("event:")) {
      live = appendEvent(live, {
        source,
        content: block.parts[0]!.content,
        data: null,
        timestamp: block.timestamp,
        senderAgentId: block.senderAgentId,
      })
      continue
    }
    live = [...live, block]
  }
  return live
}

test("live replay matches the reloaded order", () => {
  const scenarios: MessageBlock[][] = [
    [user(4, "q1"), nova(5, "a1"), event(6, "w1"), event(7, "w2")],
    [user(4, "q1"), nova(5, "a1"), event(6, "w1"), event(7, "w2"), user(8, "q2"), nova(9, "a2")],
    [user(4, "q1"), nova(5, "a1"), event(6, "w1"), user(8, "q2"), nova(9, "a2"), event(10, "w2")],
    [event(1, "w1"), nova(3, "beat1"), event(4, "w2"), nova(5, "beat2"), event(6, "w3")],
    [event(1, "w0"), user(4, "q1"), nova(5, "a1")],
  ]
  for (const blocks of scenarios) {
    assert.equal(shape(replayLive(blocks)), shape(orderMessages(blocks)))
  }
})

test("a burst during a streaming reply stays one row and does not move", () => {
  // The reported bug: three events landing mid-reply drew one dot per row, then
  // jumped to the other side of the message when a fourth arrived.
  const streaming: MessageBlock = {
    id: "live", role: "assistant", timestamp: at(5),
    parts: [{ type: "text", content: "a1", isPartial: true }],
  }
  let live: MessageBlock[] = [user(4, "q1"), streaming]
  for (const [i, minute] of [6, 7, 8, 9].entries()) {
    live = appendEvent(live, { source: "event:weather", content: `w${i}`, data: null, timestamp: at(minute) })
  }
  assert.equal(shape(live), "USER(q1) NOVA(a1) [w0+w1+w2+w3]")
})

test("live events use timestamps when relayed sockets arrive out of order", () => {
  let live: MessageBlock[] = [user(4, "q1"), nova(8, "a1")]
  live = appendEvent(live, { source: "event:weather", content: "late", data: null, timestamp: at(9) })
  live = appendEvent(live, { source: "event:weather", content: "early", data: null, timestamp: at(6) })
  assert.equal(shape(live), "USER(q1) [early] NOVA(a1) [late]")
})

test("tool activity streamed after an event stays after it", () => {
  const tool = (minute: number, name: string): ChatEvent => ({
    type: "tool_use",
    toolName: name,
    timestamp: at(minute),
    messageUid: "turn",
  })
  let live: MessageBlock[] = [user(4, "q1")]
  live = processStreamEvent(live, true, tool(5, "before")).messages
  live = appendEvent(live, { source: "event:weather", content: "rain", data: null, timestamp: at(6) })
  live = processStreamEvent(live, true, tool(7, "after")).messages

  assert.deepEqual(live.map((block) => block.parts[0]?.toolName ?? block.role), ["user", "before", "event:weather", "after"])
})
