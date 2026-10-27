import type { Artifact, ArtifactLoadParams, ArtifactLoader, ArtifactOrigin, ArtifactPage } from "@redbamboo/chat"

/** Nova keeps agent-written files out of the panel: every memory write would bury the real documents. */
export const NOVA_HIDDEN_ARTIFACT_ORIGINS: readonly ArtifactOrigin[] = ["tool-write"]

/** Shape of `GET /api/apps/nova/discussions/{id}/artifacts`: RedCompute's page, or an empty one before a session exists. */
interface NovaArtifactsResponse extends Omit<ArtifactPage, "sessionId" | "epoch" | "extractorVersion"> {
  sessionId: string | null
  epoch: string | null
  extractorVersion: number | null
}

/** Epoch the panel reports while a discussion has no session yet; the first real page is a new epoch and restarts the load. */
export const NO_SESSION_EPOCH = "nova:no-session"

export function artifactsPath(discussionId: string, params: ArtifactLoadParams): string {
  const query = new URLSearchParams()
  if (params.origins?.length) query.set("origins", params.origins.join(","))
  if (params.sinceSequence !== undefined) query.set("sinceSequence", String(params.sinceSequence))
  if (params.limit !== undefined) query.set("limit", String(params.limit))
  if (params.cursor) query.set("cursor", params.cursor)
  const search = query.toString()
  return `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/artifacts${search ? `?${search}` : ""}`
}

export function createNovaArtifactsLoader(
  discussionId: string,
  get: <T>(path: string) => Promise<T>,
): ArtifactLoader {
  return async params => {
    const page = await get<NovaArtifactsResponse>(artifactsPath(discussionId, params))
    return {
      ...page,
      sessionId: page.sessionId ?? "",
      epoch: page.epoch ?? NO_SESSION_EPOCH,
      extractorVersion: page.extractorVersion ?? 0,
    }
  }
}

/**
 * Where an artifact opens in the browser. Nova's proxy targets and `/api/...` paths are same-origin
 * as they are; an input attachment resolves exactly as the composer and attachment cards resolve
 * `UploadedAttachment.downloadUrl`, which is RedCompute's own path on this origin. Anything else
 * (entity hrefs, local paths, Leaf-internal links) has no media URL and the panel handles it itself.
 */
export function resolveNovaArtifactTarget(artifact: Pick<Artifact, "target">): string | null {
  const target = artifact.target
  if (/^https?:\/\//i.test(target)) return target
  if (target.startsWith("//")) return null
  if (target.startsWith("/api/") || target.startsWith("/ai-session/input-attachments/")) return target
  return null
}
