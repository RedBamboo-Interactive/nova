import assert from "node:assert/strict"
import test from "node:test"
import type { MessageBlock } from "@redbamboo/chat"
import { LatestTaskCoordinator } from "./latest-task-coordinator.ts"
import type { DiscussionHistoryOverlay } from "./types.ts"
import {
  accumulateHistoryOverlays,
  mergePagedDiscussionAndSessionBlocks,
} from "./discussion-transcript.ts"
import {
  HistoryLifecycleTombstones,
  historyRevalidationDirection,
  invalidateHistoryGeneration,
  isCurrentHistoryGeneration,
  shouldAccumulatePushedHistoryOverlay,
  shouldCatchUpHistory,
} from "./discussion-history-page.ts"

function overlay(
  id: string,
  messageUid: string | null,
  source: string,
  content: string,
): DiscussionHistoryOverlay {
  return {
    id,
    messageUid,
    role: source.startsWith("event:") ? "assistant" : "user",
    parts: [{ type: "text", content }],
    timestamp: "2026-09-12T12:00:00Z",
    source,
  }
}

function block(id: string, source?: string): MessageBlock {
  return {
    id,
    role: "assistant",
    parts: [{ type: "text", content: id }],
    timestamp: "2026-09-12T12:00:00Z",
    metadata: source ? { source, messageUid: id } : { messageUid: id },
  }
}

test("overlay accumulation is stable-id based and retry idempotent", () => {
  const first = overlay("record-1", "uid-1", "user-message", "old")
  const retry = overlay("record-2", "uid-1", "user-message", "durable")
  const accumulated = accumulateHistoryOverlays(
    accumulateHistoryOverlays(new Map(), [first]),
    [retry],
  )

  assert.equal(accumulated.size, 1)
  assert.equal([...accumulated.values()][0]?.parts[0]?.content, "durable")
})

test("a pushed overlay racing the initial V2 page survives its older snapshot", () => {
  const pushed = overlay("event-1", "event-1", "event:weather", "arrived live")
  let accumulated = new Map<string, DiscussionHistoryOverlay>()

  // The server has already captured an empty snapshot, but the response has
  // not established the discussion's paging mode when this websocket arrives.
  assert.equal(shouldAccumulatePushedHistoryOverlay(undefined), true)
  accumulated = accumulateHistoryOverlays(accumulated, [pushed])
  accumulated = accumulateHistoryOverlays(accumulated, [])

  assert.deepEqual([...accumulated.values()], [pushed])
  assert.equal(shouldAccumulatePushedHistoryOverlay("v2"), true)
  assert.equal(shouldAccumulatePushedHistoryOverlay("legacy"), false)
})

test("V2 merge lets canonical transcript replace the same user bridge", () => {
  const bridge = block("turn-1", "user-message")
  bridge.role = "user"
  const canonical = block("turn-1")
  canonical.role = "user"

  assert.deepEqual(mergePagedDiscussionAndSessionBlocks([bridge], [canonical]), [canonical])
})

test("V2 merge keeps the richer event overlay over an injected transcript copy", () => {
  const event = block("event-1", "event:weather")
  const injectedCopy = block("event-1")

  assert.deepEqual(mergePagedDiscussionAndSessionBlocks([event], [injectedCopy]), [event])
})

test("V2 merge does not collapse equal content at equal timestamps", () => {
  const a = block("turn-a")
  const b = block("turn-b")
  a.parts[0]!.content = "same"
  b.parts[0]!.content = "same"

  assert.deepEqual(mergePagedDiscussionAndSessionBlocks([], [a, b]), [a, b])
})

test("a loaded V2 window catches up after its anchor instead of unioning only the newest page", () => {
  // If 1..500 are loaded and 1001..1500 are now newest, a newest-page union
  // would preserve the prefix while silently skipping 501..1000.
  assert.equal(historyRevalidationDirection("v2", "cursor-500"), "after")
  assert.equal(shouldCatchUpHistory("v2", "cursor-500", false), true)
  assert.equal(historyRevalidationDirection("v2", null), "newest")
  assert.equal(historyRevalidationDirection("legacy", "ignored"), "newest")
})

test("cleanup leaves a monotonic tombstone that rejects an in-flight page", () => {
  const generations: Record<string, number> = { rotated: 7 }
  const capturedGeneration = generations.rotated!

  assert.equal(invalidateHistoryGeneration(generations, "rotated"), 8)
  assert.equal(
    isCurrentHistoryGeneration(generations, "rotated", capturedGeneration),
    false,
  )
  assert.equal(isCurrentHistoryGeneration(generations, "rotated", 8), true)
})

test("a catch-up queued before rotation cannot run after retirement", async () => {
  const lifecycle = new HistoryLifecycleTombstones()
  const coordinator = new LatestTaskCoordinator<string>()
  let releaseActive!: () => void
  const activeGate = new Promise<void>((resolve) => { releaseActive = resolve })
  const mutations: string[] = []

  const active = coordinator.run("old-discussion", async () => {
    await activeGate
  })
  coordinator.run("old-discussion", async () => {
    if (lifecycle.canRun("old-discussion")) mutations.push("old-discussion")
  })

  lifecycle.retire("old-discussion")
  lifecycle.revive("replacement-discussion")
  releaseActive()
  await active

  assert.deepEqual(mutations, [])
  assert.equal(lifecycle.canRun("replacement-discussion"), true)
})
