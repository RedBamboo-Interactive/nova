export interface FloatingOpenSelectionInput {
  explicitDiscussionId?: string
  openerPathname: string
  persistedDiscussionId: string | null
  surfaceAlreadyOpen: boolean
  surfaceOpening: boolean
}

export function discussionIdFromNovaChatPath(pathname: string): string | null {
  const match = pathname.match(/^\/apps\/nova\/chat\/([^/]+)\/?$/)
  if (!match?.[1]) return null
  try {
    return decodeURIComponent(match[1])
  } catch {
    return null
  }
}

/**
 * Float and Normal own independent selections once the surface exists. A new
 * Float, however, starts from the discussion the opener is currently showing.
 * Outside Nova there is no opener selection, so restore Float's own last one.
 */
export function resolveFloatingOpenSelection({
  explicitDiscussionId,
  openerPathname,
  persistedDiscussionId,
  surfaceAlreadyOpen,
  surfaceOpening,
}: FloatingOpenSelectionInput): string | null {
  if (surfaceAlreadyOpen || surfaceOpening) return persistedDiscussionId
  return explicitDiscussionId
    ?? discussionIdFromNovaChatPath(openerPathname)
    ?? persistedDiscussionId
}
