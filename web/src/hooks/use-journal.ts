import { useCallback, useEffect, useRef, useState } from "react"
import { useUiEnvironment } from "@redbamboo/ui"
import { fetchJournalDay } from "../lib/journal-api"
import { JOURNAL_REFRESH_MS, shouldRefreshJournal } from "../lib/journal"
import type { JournalDay } from "../lib/journal-types"

interface JournalDayState {
  key: string
  day: JournalDay | null
  error: string | null
}

/**
 * One journal day. It reads when opened; today reads again about once a minute while it can be seen,
 * and on coming back to the page. Stale answers lose to newer ones, and a failed refresh keeps what
 * is already on screen.
 */
export function useJournalDay(agent: string | null, date: string | null) {
  const environment = useUiEnvironment()
  const key = `${agent ?? ""}|${date ?? ""}`
  const [state, setState] = useState<JournalDayState>({ key, day: null, error: null })
  const sequence = useRef(0)

  const refresh = useCallback(async () => {
    const current = ++sequence.current
    try {
      const day = await fetchJournalDay(agent, date)
      if (current === sequence.current) setState({ key, day, error: null })
    } catch (error) {
      if (current !== sequence.current) return
      const message = error instanceof Error ? error.message : "The journal couldn't be read"
      setState((previous) => ({ key, day: previous.key === key ? previous.day : null, error: message }))
    }
  }, [agent, date, key])

  useEffect(() => { void refresh() }, [refresh])

  const shown = state.key === key ? state : { key, day: null, error: null }
  const shownDay = useRef(shown.day)
  shownDay.current = shown.day

  useEffect(() => {
    const { document, window } = environment
    const tick = () => {
      if (shouldRefreshJournal(shownDay.current, document.visibilityState)) void refresh()
    }
    const timer = window.setInterval(tick, JOURNAL_REFRESH_MS)
    document.addEventListener("visibilitychange", tick)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener("visibilitychange", tick)
    }
  }, [environment, refresh])

  return { day: shown.day, error: shown.error, refresh }
}
