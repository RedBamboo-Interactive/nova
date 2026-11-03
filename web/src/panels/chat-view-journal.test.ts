import { readFileSync } from "node:fs"
import assert from "node:assert/strict"
import { test } from "node:test"

const source = readFileSync(new URL("./ChatView.tsx", import.meta.url), "utf8")

test("the chat stays in the tree under the Journal, hidden and never unmounted", () => {
  const detail = source.slice(source.indexOf("detail={"), source.indexOf("<ArtifactsSurface"))

  // One constant wrapper holds the chat on every route, so opening the Journal can't remount it.
  assert.match(detail, /journalOpen \? " invisible" : ""\}`\}>\{chatArea\}<\/div>/)
  // The Journal is added beside it, never in its place.
  assert.match(detail, /\{journalOpen && \(\s*<div data-slot="journal-surface"/)
  assert.doesNotMatch(detail, /journalOpen \? [^"]*<JournalView/)
  assert.doesNotMatch(detail, /journalOpen \? null/)
  assert.doesNotMatch(detail, /key=\{/)
})

test("the chat area itself doesn't depend on the Journal being open", () => {
  const start = source.indexOf("const chatArea = ")
  const area = source.slice(start, source.indexOf("return (", start))

  assert.notEqual(start, -1)
  assert.doesNotMatch(area, /journalOpen/)
  assert.match(area, /<ChatPanel/)
  assert.doesNotMatch(area, /<ChatPanel\s+key=/)
})

test("the message link goes through the existing controller and is removed with replace", () => {
  const start = source.indexOf("const linkedMessage = ")
  const effect = source.slice(start, source.indexOf("const handleFocusMessageResult", start))

  assert.match(effect, /goToMessage\.go\(step\.uid\)/)
  assert.match(effect, /replace: true/)
  // It waits for a switch to finish, and it runs after the effect that cancels on one.
  assert.match(effect, /isLoadingMessages \|\| isResolvingDiscussion/)
  assert.ok(source.indexOf("goToMessage.cancel()\n") < start || source.indexOf("goToMessage.cancel()") < start)
})

test("the Journal writes no read marker, and the pause is in place before Live is selected under it", () => {
  const hook = readFileSync(new URL("../hooks/use-discussions.ts", import.meta.url), "utf8")
  const start = hook.indexOf("const acknowledgeRead = useCallback(")

  // Every acknowledgement goes through this one guard.
  assert.match(hook.slice(start, start + 200), /if \(readAcknowledgementPausedRef\.current\) return/)
  assert.equal((hook.match(/\/read`/g) ?? []).length, 1)

  const pause = source.indexOf("setReadAcknowledgementPaused(journalOpen, true)")
  const selection = source.indexOf("selectDiscussion(synchronizedDiscussionId)")
  assert.notEqual(pause, -1)
  assert.ok(pause < selection)
  assert.match(source, /setReadAcknowledgementPaused\(false, false\)/)
})

test("the Journal tab is the third trigger with its own icon and a token colour", () => {
  const live = source.indexOf('value="live"')
  const heartbeat = source.indexOf('value="heartbeat"')
  const journal = source.indexOf('value="journal"')

  assert.ok(live !== -1 && live < heartbeat && heartbeat < journal)
  assert.match(source.slice(journal, journal + 500), /ph-notebook/)
  assert.match(source.slice(journal, journal + 500), /var\(--color-accent-purple\)/)
})
