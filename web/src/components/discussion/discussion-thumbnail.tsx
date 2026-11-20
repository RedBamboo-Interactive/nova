import { createContext, useContext, useEffect, useRef, useState } from "react"
import { api, novaExecution } from "../../lib/api"
import { groupingIcon } from "../../lib/discussion-grouping"
import type { DiscussionInfo } from "../../lib/types"

const imageLimit = 10 * 1024 * 1024
const cacheLimit = 32 * 1024 * 1024
const thumbnailLimit = 32

class DiscussionThumbnailCache {
  private entries = new Map<string, { url: string; size: number }>()
  private size = 0

  get(id: string) { return this.entries.get(id)?.url }

  store(id: string, blob: Blob) {
    const existing = this.get(id)
    if (existing) return existing
    while (this.size + blob.size > cacheLimit || this.entries.size >= thumbnailLimit) {
      const oldest = this.entries.keys().next()
      if (oldest.done) break
      this.delete(oldest.value)
    }
    const url = URL.createObjectURL(blob)
    this.entries.set(id, { url, size: blob.size })
    this.size += blob.size
    return url
  }

  delete(id: string, expectedUrl?: string) {
    const entry = this.entries.get(id)
    if (!entry || (expectedUrl && expectedUrl !== entry.url)) return
    URL.revokeObjectURL(entry.url)
    this.entries.delete(id)
    this.size -= entry.size
  }

  retain(ids: ReadonlySet<string>) {
    for (const id of this.entries.keys()) if (!ids.has(id)) this.delete(id)
  }
}

export const DiscussionThumbnailCacheContext = createContext<DiscussionThumbnailCache | null>(null)

/** ChatView owns the cache while the mobile sidebar mounts and unmounts. */
export function useDiscussionThumbnailCache(discussions: DiscussionInfo[]) {
  const [cache] = useState(() => new DiscussionThumbnailCache())
  useEffect(() => {
    const available = discussions.filter(d => d.type === "chat" && !d.confidential
      && d.status !== "archived" && d.status !== "archiving")
    cache.retain(new Set(available.map(d => d.id)))
  }, [cache, discussions])
  useEffect(() => () => cache.retain(new Set()), [cache])
  return cache
}

/** Read authorized artifacts lazily, reusing thumbnails across sidebar mounts. */
export function DiscussionThumbnail({ id, icon }: { id: string; icon: string }) {
  const cache = useContext(DiscussionThumbnailCacheContext)
  const ref = useRef<HTMLSpanElement>(null)
  const [loaded, setLoaded] = useState<{ id: string; url: string } | null>(null)
  const source = cache?.get(id) ?? (loaded?.id === id ? loaded.url : null)
  useEffect(() => {
    const element = ref.current
    if (!element || cache?.get(id)) return
    let started = false
    let temporaryUrl: string | null = null
    const abort = new AbortController()
    const load = async () => {
      if (started) return
      started = true
      const route = "/api/apps/nova/discussions/" + encodeURIComponent(id)
      try {
        const page = await api.get<{ items: Array<{ kind: string; target: string }> }>(route + "/artifacts?kinds=image&limit=1", abort.signal)
        const target = page.items.find(item => item.kind === "image")?.target
        if (abort.signal.aborted || !target || !(target.startsWith("/api/assets/")
          || target.startsWith(route + "/messages/")) || target.includes("\\") || target.includes("..")) return
        const response = await novaExecution.fetch(target, { credentials: "include", signal: abort.signal })
        if (!response.ok || !response.headers.get("content-type")?.startsWith("image/")) return
        const blob = await response.blob()
        if (abort.signal.aborted || blob.size > imageLimit) return
        const url = cache ? cache.store(id, blob) : (temporaryUrl = URL.createObjectURL(blob))
        setLoaded({ id, url })
      } catch { /* An unavailable artifact keeps its icon. */ }
    }
    const observer = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { observer?.disconnect(); void load() }
    }) : null
    if (observer) observer.observe(element)
    else void load()
    return () => { abort.abort(); observer?.disconnect(); if (temporaryUrl) URL.revokeObjectURL(temporaryUrl) }
  }, [cache, id])
  return <span ref={ref} className="absolute inset-0 flex items-center justify-center overflow-hidden rounded-lg" aria-hidden="true">
    {source ? <img src={source} alt="" className="h-full w-full object-cover" onError={() => { cache?.delete(id, source); setLoaded(null) }} />
      : <i className={"ph ph-" + groupingIcon(icon) + " text-base text-text-muted"} />}
  </span>
}
