import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { api } from "../lib/api"
import { groupingInputKey, groupingStillVisible, type DiscussionGroupingView } from "../lib/discussion-grouping"
import type { DiscussionInfo } from "../lib/types"

/** Optional work behind the list. A status/read/activity event never reruns the classifier. */
export function useDiscussionGrouping(enabled: boolean, discussions: DiscussionInfo[]) {
  const [view, setView] = useState<DiscussionGroupingView | null>(null)
  const [loading, setLoading] = useState(false)
  const [unavailable, setUnavailable] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const generation = useRef(0)
  const inputKey = groupingInputKey(discussions)
  const latestDiscussions = useRef(discussions)
  latestDiscussions.current = discussions
  useEffect(() => {
    const current = ++generation.current
    let cancelled = false
    if (!enabled) { setLoading(false); setUnavailable(false); return }
    const accept = (result: DiscussionGroupingView) => {
      if (cancelled || generation.current !== current) return false
      setView(groupingStillVisible(result, latestDiscussions.current) ? result : null)
      return true
    }
    const timer = setTimeout(() => {
      setLoading(true)
      setUnavailable(false)
      void (async () => {
        try {
          const cached = await api.get<DiscussionGroupingView>("/api/apps/nova/discussion-groups")
          if (!accept(cached)) return
          if (cached.fingerprint !== cached.sourceFingerprint) {
            if (cached.retryAfterSeconds > 0) { setUnavailable(true); return }
            const updated = await api.post<DiscussionGroupingView>("/api/apps/nova/discussion-groups/refresh")
            if (accept(updated)) setUnavailable(updated.fingerprint !== updated.sourceFingerprint)
          }
        } catch { if (!cancelled && generation.current === current) setUnavailable(true) }
        finally { if (!cancelled && generation.current === current) setLoading(false) }
      })()
    }, 600)
    return () => { cancelled = true; clearTimeout(timer) }
  }, [enabled, inputKey, attempt])
  const visible = useMemo(() => enabled && view && groupingStillVisible(view, discussions) ? view : null,
    [enabled, view, discussions])
  return { view: visible, loading, unavailable, retry: useCallback(() => setAttempt(v => v + 1), []) }
}
