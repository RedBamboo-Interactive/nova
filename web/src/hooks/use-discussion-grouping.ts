import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { api } from "../lib/api"
import { groupingAutoRetryDelayMs, groupingFailureVisible, groupingInputKey, groupingStillVisible, type DiscussionGroupingView } from "../lib/discussion-grouping"
import { isDiscussionArchivePending } from "../lib/discussion-list-store"
import type { DiscussionInfo } from "../lib/types"

/** Retry delay when RedLeaf could not be reached at all, e.g. during a restart. */
const UNREACHABLE_RETRY_SECONDS = 30

/** Optional work behind the list. A status/read/activity event never reruns the classifier. */
export function useDiscussionGrouping(enabled: boolean, discussions: DiscussionInfo[]) {
  const [view, setView] = useState<DiscussionGroupingView | null>(null)
  const [loading, setLoading] = useState(false)
  const [unavailable, setUnavailable] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const generation = useRef(0)
  // Consecutive failed passes, kept across input changes so a stuck provider still surfaces.
  const failures = useRef(0)
  const inputKey = groupingInputKey(discussions)
  const latestDiscussions = useRef(discussions)
  latestDiscussions.current = discussions
  useEffect(() => {
    const current = ++generation.current
    let cancelled = false
    const abort = new AbortController()
    if (!enabled) { failures.current = 0; setLoading(false); setUnavailable(false); return }
    const accept = (result: DiscussionGroupingView) => {
      if (cancelled || generation.current !== current) return false
      if (groupingStillVisible(result, latestDiscussions.current, isDiscussionArchivePending)) setView(result)
      return true
    }
    let refreshTimer: ReturnType<typeof setTimeout> | undefined
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    const isCurrent = () => !cancelled && generation.current === current
    const succeed = () => { failures.current = 0; setUnavailable(false) }
    const fail = (retryAfterSeconds: number, newFailure: boolean) => {
      if (!isCurrent()) return
      if (newFailure) failures.current += 1
      setUnavailable(groupingFailureVisible(failures.current))
      const delay = groupingAutoRetryDelayMs(failures.current, retryAfterSeconds)
      if (delay !== null) retryTimer = setTimeout(() => setAttempt(v => v + 1), delay)
    }
    setLoading(false)
    setUnavailable(groupingFailureVisible(failures.current))
    void api.get<DiscussionGroupingView>("/api/apps/nova/discussion-groups", abort.signal).then(cached => {
      if (!accept(cached)) return
      if (cached.fingerprint === cached.sourceFingerprint) { succeed(); return }
      // A backoff seen on read is the failure already counted here, unless another client or tab hit it.
      if (cached.retryAfterSeconds > 0) { fail(cached.retryAfterSeconds, failures.current === 0); return }
      refreshTimer = setTimeout(() => {
        setLoading(true)
        void api.post<DiscussionGroupingView>("/api/apps/nova/discussion-groups/refresh", undefined, abort.signal)
          // A stale result only means the list moved on during the pass; the next input change refreshes it.
          .then(updated => {
            if (!accept(updated)) return
            if (updated.retryAfterSeconds > 0) fail(updated.retryAfterSeconds, true)
            else succeed()
          })
          .catch(() => fail(UNREACHABLE_RETRY_SECONDS, true))
          .finally(() => { if (isCurrent()) setLoading(false) })
      }, 600)
    }).catch(() => fail(UNREACHABLE_RETRY_SECONDS, true))
    return () => { cancelled = true; clearTimeout(refreshTimer); clearTimeout(retryTimer); abort.abort() }
  }, [enabled, inputKey, attempt])
  const visible = useMemo(() => enabled && view && groupingStillVisible(view, discussions, isDiscussionArchivePending) ? view : null,
    [enabled, view, discussions])
  return { view: visible, loading, unavailable, retry: useCallback(() => setAttempt(v => v + 1), []) }
}
