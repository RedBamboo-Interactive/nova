import { test } from "node:test"
import assert from "node:assert/strict"
import { createDiscussionLookup, unlistedIds } from "./discussion-lookup.ts"
import type { DiscussionInfo } from "./types.ts"

const archived = { id: "d1bde3b4", title: "Attach Skills to a discussion", status: "archived" } as DiscussionInfo

test("only the ids the client doesn't list are looked up, each once", () => {
  const listed = new Set(["open1"])
  assert.deepEqual(unlistedIds(["open1", "d1bde3b4", "a759b194", "d1bde3b4", ""], id => listed.has(id)), ["d1bde3b4", "a759b194"])
})

test("a discussion is read once however many reports name it", async () => {
  let reads = 0
  const lookup = createDiscussionLookup(async () => { reads++; return archived })
  const [a, b] = await Promise.all([lookup("d1bde3b4"), lookup("d1bde3b4")])
  assert.equal(a?.title, "Attach Skills to a discussion")
  assert.equal(b, a)
  assert.equal(await lookup("d1bde3b4"), a)
  assert.equal(reads, 1)
})

test("a refusal is remembered, a network failure is tried again", async () => {
  let reads = 0
  const refused = createDiscussionLookup(async () => { reads++; throw Object.assign(new Error("forbidden"), { status: 403 }) })
  assert.equal(await refused("x"), null)
  assert.equal(await refused("x"), null)
  assert.equal(reads, 1)

  let attempts = 0
  const flaky = createDiscussionLookup(async () => { if (attempts++ === 0) throw new Error("offline"); return archived })
  assert.equal(await flaky("d1bde3b4"), null)
  assert.equal((await flaky("d1bde3b4"))?.id, "d1bde3b4")
})
