import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { api } from "../lib/api"
import { groupingInputKey, groupingStillVisible, type DiscussionGroupingView } from "../lib/discussion-grouping"
import { isDiscussionArchivePending } from "../lib/discussion-list-store"
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
    const abort = new AbortController()
    if (!enabled) { setLoading(false); setUnavailable(false); return }
    const accept = (result: DiscussionGroupingView) => {
      if (cancelled || generation.current !== current) return false
      setView(groupingStillVisible(result, latestDiscussions.current, isDiscussionArchivePending) ? result : null)
      return true
    }
    let refreshTimer: ReturnType<typeof setTimeout> | undefined
    const isCurrent = () => !cancelled && generation.current === current
    setLoading(false)
    setUnavailable(false)
    void api.get<DiscussionGroupingView>("/api/apps/nova/discussion-groups", abort.signal).then(cached => {
      if (!accept(cached) || cached.fingerprint === cached.sourceFingerprint) return
      if (cached.retryAfterSeconds > 0) { setUnavailable(true); return }
      refreshTimer = setTimeout(() => {
        setLoading(true)
        void api.post<DiscussionGroupingView>("/api/apps/nova/discussion-groups/refresh", undefined, abort.signal)
          .then(updated => { if (accept(updated)) setUnavailable(updated.fingerprint !== updated.sourceFingerprint) })
          .catch(() => { if (isCurrent()) setUnavailable(true) })
          .finally(() => { if (isCurrent()) setLoading(false) })
      }, 600)
    }).catch(() => { if (isCurrent()) setUnavailable(true) })
    return () => { cancelled = true; clearTimeout(refreshTimer); abort.abort() }
  }, [enabled, inputKey, attempt])
  const visible = useMemo(() => enabled && view && groupingStillVisible(view, discussions, isDiscussionArchivePending) ? view : null,
    [enabled, view, discussions])
  return { view: visible, loading, unavailable, retry: useCallback(() => setAttempt(v => v + 1), []) }
}
