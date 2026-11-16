import assert from "node:assert/strict"
import test from "node:test"
import {
  computeJobHref,
  createDiscussionJobsTransport,
  discussionJobPath,
  discussionJobsPath,
  jobFrameConcernsDiscussion,
  stopDiscussionJob,
} from "./discussion-jobs.ts"

test("every list carries its since bound and the window's end when there is one", () => {
  assert.equal(
    discussionJobsPath("ab12", { since: "2026-11-16T10:00:00.000Z" }),
    "/api/apps/nova/discussions/ab12/jobs?since=2026-11-16T10%3A00%3A00.000Z",
  )
  assert.equal(
    discussionJobsPath("a/b", { since: "2026-11-16T10:00:00Z", until: "2026-11-16T11:00:00Z" }),
    "/api/apps/nova/discussions/a%2Fb/jobs?since=2026-11-16T10%3A00%3A00Z&until=2026-11-16T11%3A00%3A00Z",
  )
  assert.equal(discussionJobPath("ab12", "j-1"), "/api/apps/nova/discussions/ab12/jobs/j-1")
  assert.equal(computeJobHref("j 1"), "/apps/compute/jobs?select=j%201")
})

test("the transport reads Nova's routes with the abort signal and never a RedCompute URL", async () => {
  const calls: Array<{ path: string; signal: AbortSignal | null | undefined }> = []
  const transport = createDiscussionJobsTransport("ab12", async (path, init) => {
    calls.push({ path, signal: init.signal })
    if (path.endsWith("/jobs/gone")) return new Response("{}", { status: 404 })
    if (path.includes("/jobs/")) return Response.json({ id: "j-1", status: "Running" })
    return Response.json({ items: [{ id: "j-1", status: "Running" }], since: "x" })
  })
  const signal = new AbortController().signal

  assert.deepEqual(await transport.list({ since: "2026-11-16T10:00:00Z" }, signal), [{ id: "j-1", status: "Running" }])
  assert.deepEqual(await transport.get("j-1", signal), { id: "j-1", status: "Running" })
  assert.equal(await transport.get("gone", signal), null)
  assert.ok(calls.every(call => call.path.startsWith("/api/apps/nova/discussions/ab12/jobs") && call.signal === signal))
})

test("a refused or failed read is an error, not an empty list", async () => {
  const transport = createDiscussionJobsTransport("ab12", async () => new Response("{}", { status: 503 }))
  await assert.rejects(transport.list({ since: "2026-11-16T10:00:00Z" }, new AbortController().signal), /HTTP 503/)
})

test("Stop posts to the job's own cancel route", async () => {
  const posted: string[] = []
  await stopDiscussionJob("ab12", "j-1", async path => { posted.push(path) })
  assert.deepEqual(posted, ["/api/apps/nova/discussions/ab12/jobs/j-1/cancel"])
})

test("a frame may concern the discussion when its provenance names it and its own session", () => {
  const discussion = { id: "ab12", sessionId: "s-1" }
  const frame = (...context: Array<{ kind: string; id: string }>) => ({ id: "j", creationProvenance: { context } })

  assert.equal(jobFrameConcernsDiscussion(frame({ kind: "discussion", id: "ab12" }, { kind: "session", id: "s-1" }), discussion), true)
  assert.equal(jobFrameConcernsDiscussion(frame({ kind: "discussion", id: "ab12" }), discussion), true)
  // A delegated worker names the parent discussion and its own session.
  assert.equal(jobFrameConcernsDiscussion(frame({ kind: "discussion", id: "ab12" }, { kind: "session", id: "worker" }), discussion), false)
  assert.equal(jobFrameConcernsDiscussion(frame({ kind: "discussion", id: "other" }), discussion), false)
  assert.equal(jobFrameConcernsDiscussion({ id: "j", status: "Running" }, discussion), false)
})
