export function delegationSessionPath(data: Record<string, unknown> | null): string | undefined {
  const sessionId = data?.sessionId
  if (typeof sessionId !== "string" || !sessionId.trim()) return undefined
  return `/apps/codered/sessions/${encodeURIComponent(sessionId)}`
}
