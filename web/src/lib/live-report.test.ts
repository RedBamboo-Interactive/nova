import { test } from "node:test"
import assert from "node:assert/strict"
import { groupLiveReport, liveReportAnswer, liveReportCounts, parseLiveReport, splitDiscussionIds } from "./live-report.ts"

const SOURCE = `title: While you were at German
since: 2026-11-25T09:20:00+01:00
needs 34ca9241: OpenCode only has a Cerebras credential here. | Add an Anthropic key | Keep Nova off OpenCode
done ad7278d0: Hotfix Nightly published: RedLeaf 0.1.56, launcher 0.1.41.
- moving 8978cdde: WinFsp relaunch reworked after review.
waiting 34ca9241 on the rebuild: Codex MCP fix ready and green.
repo redleaf: cb928f8f coordinates, 8978cdde writes, 2 local commits.
Also: the wake music is fixed.`

test("every line kind parses, and nothing is dropped", () => {
  const report = parseLiveReport(SOURCE)
  assert.equal(report.title, "While you were at German")
  assert.equal(report.sinceAt?.toISOString(), "2026-11-25T08:20:00.000Z")
  assert.deepEqual(report.rows.map(r => [r.state, r.discussionId]), [
    ["needs", "34ca9241"], ["done", "ad7278d0"], ["moving", "8978cdde"], ["waiting", "34ca9241"],
  ])
  assert.deepEqual(report.rows[0].options, ["Add an Anthropic key", "Keep Nova off OpenCode"])
  assert.equal(report.rows[0].text, "OpenCode only has a Cerebras credential here.")
  assert.equal(report.rows[1].text, "Hotfix Nightly published: RedLeaf 0.1.56, launcher 0.1.41.")
  assert.equal(report.rows[3].waitingOn, "the rebuild")
  assert.deepEqual(report.repos, [{ name: "redleaf", text: "cb928f8f coordinates, 8978cdde writes, 2 local commits." }])
  assert.deepEqual(report.notes, ["Also: the wake music is fixed."])
})

test("a block still streaming in parses every prefix without throwing", () => {
  for (let end = 0; end <= SOURCE.length; end++) {
    const report = parseLiveReport(SOURCE.slice(0, end))
    assert.ok(report.rows.length <= 4)
  }
})

test("a since that doesn't parse is kept as written but gives no facts window", () => {
  const report = parseLiveReport("since: this morning\ndone ad7278d0: shipped")
  assert.equal(report.since, "this morning")
  assert.equal(report.sinceAt, undefined)
})

test("only waiting rows carry what they wait on, and only needs rows carry answers", () => {
  const report = parseLiveReport("done ad7278d0 on nothing: shipped | not an option")
  assert.equal(report.rows[0].waitingOn, undefined)
  assert.equal(report.rows[0].options, undefined)
  assert.equal(report.rows[0].text, "shipped")
})

test("rows group under their workstream in first-mention order, ungrouped last, needs pinned out", () => {
  const report = parseLiveReport(SOURCE)
  const groups = groupLiveReport(report.rows, id => id === "ad7278d0" ? { name: "Storage" } : id === "8978cdde" ? { name: "Linux" } : null)
  assert.deepEqual(groups.map(g => [g.name, g.rows.map(r => r.discussionId)]), [
    ["Storage", ["ad7278d0"]], ["Linux", ["8978cdde"]], [null, ["34ca9241"]],
  ])
  assert.deepEqual(liveReportCounts(report.rows), { needs: 1, done: 1, moving: 1, waiting: 1 })
})

test("an answer names the discussion it is about", () => {
  assert.equal(liveReportAnswer("Initiative: MCP servers on demand", "34ca9241", "Keep Nova off OpenCode"),
    'On "Initiative: MCP servers on demand": Keep Nova off OpenCode')
  assert.equal(liveReportAnswer(null, "34ca9241", "Yes"), 'On "34ca9241": Yes')
})

test("known discussion ids inside free text become links, others stay text", () => {
  const parts = splitDiscussionIds("cb928f8f coordinates, deadbeef is not one", id => id === "cb928f8f")
  assert.deepEqual(parts, [{ text: "cb928f8f", discussionId: "cb928f8f" }, { text: " coordinates, deadbeef is not one" }])
})
