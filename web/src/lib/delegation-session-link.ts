export function delegationSessionPath(data: Record<string, unknown> | null): string | undefined {
  const sessionId = data?.sessionId
  if (typeof sessionId !== "string" || !sessionId.trim()) return undefined
  return `/apps/codered/sessions/${encodeURIComponent(sessionId)}`
}

/** Event keys that name a delegated Code session: the delegation marker and its status/approval notices. */
export const DELEGATION_EVENT_KEYS: readonly string[] = ["delegation", "delegate"]

/** Where an event about a delegated session opens, or undefined when it isn't one or names no session. */
export function delegationEventPath(key: string, data: Record<string, unknown> | null): string | undefined {
  return DELEGATION_EVENT_KEYS.includes(key) ? delegationSessionPath(data) : undefined
}
