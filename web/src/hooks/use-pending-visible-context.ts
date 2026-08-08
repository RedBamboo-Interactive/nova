import { useEffect, useMemo, useRef, useSyncExternalStore } from "react"
import {
  PendingVisibleContextStore,
  type PendingVisibleContextEntry,
} from "../lib/pending-visible-context-store"

export interface PendingVisibleContextController {
  revision: number
  get: (discussionId: string | null | undefined) => PendingVisibleContextEntry | null
  set: (discussionId: string, entry: PendingVisibleContextEntry) => void
  consume: (discussionId: string) => PendingVisibleContextEntry | null
  clear: (discussionId: string | null | undefined) => void
}

export function usePendingVisibleContext(): PendingVisibleContextController {
  const storeRef = useRef<PendingVisibleContextStore | null>(null)
  if (!storeRef.current) storeRef.current = new PendingVisibleContextStore()
  const store = storeRef.current
  const revision = useSyncExternalStore(
    listener => store.subscribe(listener),
    () => store.getSnapshot(),
    () => store.getSnapshot(),
  )

  useEffect(() => () => store.dispose(), [store])

  return useMemo(() => ({
    revision,
    get: discussionId => store.get(discussionId),
    set: (discussionId, entry) => store.set(discussionId, entry),
    consume: discussionId => store.consume(discussionId),
    clear: discussionId => store.clear(discussionId),
  }), [revision, store])
}
