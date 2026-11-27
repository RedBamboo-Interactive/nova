import { useCallback, useEffect, useRef, useState } from "react"
import { api } from "../lib/api"
import { SUMMARY_SETTINGS_CHANGED, type DiscussionSummaryView } from "../lib/discussion-summaries"

export function useDiscussionSummaries(browserWindow: Window) {
  const [views, setViews] = useState<Record<string, DiscussionSummaryView>>({})
  const activeRead = useRef<AbortController | null>(null)
  const refresh = useCallback(async () => {
    activeRead.current?.abort()
    const controller = new AbortController()
    activeRead.current = controller
    try {
      const data = await api.get<{ discussions: DiscussionSummaryView[] }>("/api/apps/nova/discussion-summaries", controller.signal)
      if (!controller.signal.aborted)
        setViews(Object.fromEntries(data.discussions.map(view => [view.discussionId, view])))
    } catch {
      // An unavailable projection does not affect normal chat.
    }
  }, [])

  useEffect(() => {
    const sync = () => { void refresh() }
    sync()
    browserWindow.addEventListener(SUMMARY_SETTINGS_CHANGED, sync)
    browserWindow.addEventListener("focus", sync)
    return () => {
      activeRead.current?.abort()
      browserWindow.removeEventListener(SUMMARY_SETTINGS_CHANGED, sync)
      browserWindow.removeEventListener("focus", sync)
    }
  }, [browserWindow, refresh])

  const setOverride = useCallback(async (id: string, enabled: boolean | null) => {
    await api.put(`/api/apps/nova/discussions/${encodeURIComponent(id)}/summary/preference`, { enabled })
    await refresh()
  }, [refresh])

  return { views, refresh, setOverride }
}
