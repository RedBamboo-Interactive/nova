/**
 * The body of POST /api/apps/nova/discussions from the browser: agentId, qualityTier, provider and idempotencyKey
 * when set, then `nodeId` only when the person picked a node in "Run on". Automatic (null or absent) adds
 * nothing, so the request is exactly what it was before nodes could be chosen. No member at all: no body.
 */
export function newDiscussionBody(
  agentId?: string,
  qualityTier?: string,
  provider?: string,
  options?: { idempotencyKey?: string; nodeId?: string | null },
): Record<string, string> | undefined {
  const body: Record<string, string> = {}
  if (agentId) body.agentId = agentId
  if (qualityTier) body.qualityTier = qualityTier
  if (provider) body.provider = provider
  // A repeated request with the same key returns the same discussion instead of a second one.
  if (options?.idempotencyKey) body.idempotencyKey = options.idempotencyKey
  if (options?.nodeId) body.nodeId = options.nodeId
  return Object.keys(body).length ? body : undefined
}
