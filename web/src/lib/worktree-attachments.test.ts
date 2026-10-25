import test from "node:test"
import assert from "node:assert/strict"
import { delegationPlacement, placementLabel, readWorktrees, worktreeChipLabel, worktreeCodePath } from "./worktree-attachments.ts"

const attachment = { repositoryId: "174d9230", repositoryName: "songes-carmins", branch: "laurent/x", path: "L:/songes.worktrees/laurent-x", attachedAt: "2026-10-25T12:00:00Z" }

test("a chip reads repo · branch", () => {
  assert.equal(worktreeChipLabel(attachment), "songes-carmins · laurent/x")
})

test("a chip opens the repository in Code on the attached branch", () => {
  assert.equal(worktreeCodePath(attachment), "/apps/codered/github/174d9230?branch=laurent%2Fx")
  assert.equal(worktreeCodePath({ repositoryId: "", branch: "x" }), undefined)
})

test("a delegated worktree session shows repo · branch and the worktree badge", () => {
  assert.deepEqual(delegationPlacement({ repository: "songes", branch: "fix/y", worktreePath: "L:/w" }), { label: "songes · fix/y", worktree: true })
})

test("a branch checked out in the checkout is not a worktree", () => {
  assert.deepEqual(delegationPlacement({ repository: "songes", branch: "main", worktreePath: null }), { label: "songes · main", worktree: false })
})

test("old markers carry no branch and fall back to the bare repository", () => {
  assert.deepEqual(delegationPlacement({ repository: "songes" }), { label: "songes", worktree: false })
  assert.equal(delegationPlacement({ sessionId: "s" }), undefined)
  assert.equal(delegationPlacement(null), undefined)
})

test("labels skip missing parts", () => {
  assert.equal(placementLabel(null, "x"), "x")
  assert.equal(placementLabel("r", ""), "r")
})

test("readWorktrees ignores a missing or malformed list", () => {
  assert.deepEqual(readWorktrees(undefined), [])
  assert.deepEqual(readWorktrees([{ nope: 1 }, null, attachment]), [attachment])
})
