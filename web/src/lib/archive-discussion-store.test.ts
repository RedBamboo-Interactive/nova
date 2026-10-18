import assert from "node:assert/strict"
import test from "node:test"
import {
  beginArchiveLoad,
  canCommitDiscussionHistory,
  completeArchiveLoad,
  createArchiveContentSearchParams,
  failArchiveLoad,
  getArchiveDiscussionState,
  mergeArchivedSearchResults,
  resetArchiveDiscussionStore,
  resolveArchiveSearchEscape,
  resolveKnownDiscussion,
  setArchiveMode,
  setArchiveQuery,
  upsertArchivedDiscussion,
} from "./archive-discussion-store.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(id: string, title = id, status: DiscussionInfo["status"] = "archived"): DiscussionInfo {
  return {
    id,
    entityId: `entity-${id}`,
    title,
    titleSource: null,
    sessionId: `session-${id}`,
    status,
    type: "chat",
    createdAt: "2026-01-01T00:00:00Z",
    lastActivity: "2026-01-02T00:00:00Z",
    messageCount: 1,
    lastReadAt: null,
    conversationRevision: 1,
    readConversationRevision: 0,
    agentId: "agent-a",
  }
}

test("archive mode and its first-load state are session scoped and never false-empty", () => {
  resetArchiveDiscussionStore()
  setArchiveMode(true)
  beginArchiveLoad("", false)
  assert.equal(getArchiveDiscussionState().enabled, true)
  assert.equal(getArchiveDiscussionState().loadState, "loading")
  assert.equal(getArchiveDiscussionState().hasLoaded, false)

  failArchiveLoad("")
  assert.equal(getArchiveDiscussionState().loadState, "failed")
  beginArchiveLoad("", false)
  completeArchiveLoad([discussion("one")], "next", true, "", null, false)
  assert.equal(getArchiveDiscussionState().loadState, "ready")
  assert.equal(getArchiveDiscussionState().items.length, 1)
  beginArchiveLoad("", false)
  failArchiveLoad("")
  assert.equal(getArchiveDiscussionState().loadState, "ready")
  assert.equal(getArchiveDiscussionState().items.length, 1)
})

test("title matches rank before de-duplicated content matches with snippets", () => {
  const title = discussion("title", "Needle title")
  const duplicate = discussion("duplicate", "Also a title")
  const content = discussion("content", "Different title")
  const merged = mergeArchivedSearchResults([title, duplicate], [
    { discussionId: "duplicate", discussion: duplicate, matchCount: 2, snippets: [{ role: "user", timestamp: "", snippet: "duplicate body" }] },
    { discussionId: "content", discussion: content, matchCount: 1, snippets: [{ role: "assistant", timestamp: "", snippet: "needle in transcript" }] },
  ])

  assert.deepEqual(merged.map(item => item.id), ["title", "duplicate", "content"])
  assert.equal(merged[0].searchMatch, "title")
  assert.equal(merged[2].searchMatch, "content")
  assert.equal(merged[2].searchSnippet, "needle in transcript")
})

test("archive content search requests the chat and LIVE type scope", () => {
  const params = createArchiveContentSearchParams("needle", "agent-a")

  assert.equal(params.get("q"), "needle")
  assert.equal(params.get("status"), "archived")
  assert.equal(params.get("includeLive"), "true")
  assert.equal(params.get("includeHeartbeats"), "false")
  assert.equal(params.get("agent"), "agent-a")
})

test("archive search Escape clears a query before leaving archive mode", () => {
  assert.equal(resolveArchiveSearchEscape("needle"), "clear")
  assert.equal(resolveArchiveSearchEscape(""), "exit")
})

test("a closed direct-url record outside the open list can commit read-only history", () => {
  resetArchiveDiscussionStore()
  setArchiveMode(true)
  setArchiveQuery("another title")
  const archived = discussion("archived")
  upsertArchivedDiscussion(archived)
  const snapshot = getArchiveDiscussionState()
  const known = resolveKnownDiscussion(
    [discussion("open", "Open", "idle")],
    snapshot.knownItems,
    archived.id,
  )

  assert.equal(known, archived)
  assert.deepEqual(snapshot.items, [])
  assert.equal(canCommitDiscussionHistory(known, false, archived.sessionId), true)
  assert.equal(canCommitDiscussionHistory(known, true, archived.sessionId), false)
})
