import type { ChatJobsTransport, ComputeJobRecord } from "@redbamboo/chat"

/**
 * Nova's side of the chat's job markers: the discussion's own authorized job routes. The browser
 * never calls RedCompute for them, and job outputs come through the discussion's output route.
 */

export function discussionJobsPath(discussionId: string, window?: { since: string; until?: string }): string {
  const base = `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/jobs`
  if (!window) return base
  const query = new URLSearchParams({ since: window.since })
  if (window.until) query.set("until", window.until)
  return `${base}?${query.toString()}`
}

export function discussionJobPath(discussionId: string, jobId: string): string {
  return `${discussionJobsPath(discussionId)}/${encodeURIComponent(jobId)}`
}

/** The Compute dashboard with the job selected. */
export function computeJobHref(jobId: string): string {
  return `/apps/compute/jobs?select=${encodeURIComponent(jobId)}`
}

type Fetch = (path: string, init: RequestInit) => Promise<Response>

/** Lists and single reads with the request's abort signal; a job that is gone or not this discussion's is null. */
export function createDiscussionJobsTransport(discussionId: string, fetch: Fetch): ChatJobsTransport {
  const read = async (path: string, signal: AbortSignal): Promise<Response> => {
    const response = await fetch(path, { method: "GET", credentials: "include", signal })
    if (!response.ok && response.status !== 404)
      throw new Error(`Job read failed with HTTP ${response.status}`)
    return response
  }
  return {
    async list(window, signal) {
      const response = await read(discussionJobsPath(discussionId, window), signal)
      if (response.status === 404) return []
      const page = await response.json() as { items?: unknown }
      return Array.isArray(page.items) ? page.items as ComputeJobRecord[] : []
    },
    async get(id, signal) {
      const response = await read(discussionJobPath(discussionId, id), signal)
      if (response.status === 404) return null
      return await response.json() as ComputeJobRecord
    },
  }
}

export async function stopDiscussionJob(discussionId: string, jobId: string, post: (path: string) => Promise<unknown>): Promise<void> {
  await post(`${discussionJobPath(discussionId, jobId)}/cancel`)
}

/**
 * Whether a job frame this discussion doesn't know yet may be one of its own: its provenance names
 * this discussion, and any session it names is this discussion's. A delegated worker's jobs name the
 * parent discussion and the worker's own session, so they stay on the worker's card. Only a hint for
 * a re-read: the endpoint decides.
 */
export function jobFrameConcernsDiscussion(
  data: Record<string, unknown>,
  discussion: { id: string; sessionId?: string | null },
): boolean {
  const provenance = data.creationProvenance as { context?: unknown } | undefined
  const context = Array.isArray(provenance?.context) ? provenance.context as Array<{ kind?: unknown; id?: unknown }> : []
  let names = false
  for (const entry of context) {
    if (entry?.kind === "discussion" && entry.id === discussion.id) names = true
    if (entry?.kind === "session" && discussion.sessionId && entry.id !== discussion.sessionId) return false
  }
  return names
}
