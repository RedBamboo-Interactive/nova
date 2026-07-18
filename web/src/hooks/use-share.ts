import { useState, useCallback } from "react"
import { api } from "../lib/api"

interface ShareInfoResponse {
  active: boolean
  shareId?: string
  url?: string
  sharedAt?: string
}

interface CreateShareResult {
  shareId: string
  url: string
}

export function useShare(entityId: string | undefined) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [shareUrl, setShareUrl] = useState<string | null>(null)

  const createShare = useCallback(async () => {
    if (!entityId) return
    setLoading(true)
    setError(null)
    try {
      const result = await api.post<CreateShareResult>("/api/shares", { discussionEntityId: entityId })
      setShareUrl(result.url)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to create share link")
    } finally {
      setLoading(false)
    }
  }, [entityId])

  const revokeShare = useCallback(async () => {
    if (!entityId) return
    setError(null)
    try {
      const info = await api.get<ShareInfoResponse>(`/api/shares/info/${entityId}`)
      if (info.active && info.shareId) {
        await api.delete(`/api/shares/${info.shareId}`)
      }
      setShareUrl(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to revoke share")
    }
  }, [entityId])

  const loadShare = useCallback(async () => {
    if (!entityId) return
    try {
      const info = await api.get<ShareInfoResponse>(`/api/shares/info/${entityId}`)
      setShareUrl(info.active ? (info.url ?? null) : null)
    } catch {
      setShareUrl(null)
    }
  }, [entityId])

  const reset = useCallback(() => {
    setShareUrl(null)
    setError(null)
    setLoading(false)
  }, [])

  return { shareUrl, loading, error, createShare, revokeShare, loadShare, reset }
}
