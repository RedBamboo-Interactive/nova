import { createContext, useContext, useEffect, useRef, useState } from "react"
import { novaExecution } from "../../lib/api"
import { groupingIcon } from "../../lib/discussion-grouping"
import type { DiscussionInfo } from "../../lib/types"

const imageLimit = 10 * 1024 * 1024
const cacheLimit = 32 * 1024 * 1024
const thumbnailLimit = 32

class DiscussionThumbnailCache {
  private entries = new Map<string, { id: string; url: string; size: number }>()
  private size = 0

  get(key: string) { return this.entries.get(key)?.url }

  store(key: string, id: string, blob: Blob) {
    const existing = this.get(key)
    if (existing) return existing
    while (this.size + blob.size > cacheLimit || this.entries.size >= thumbnailLimit) {
      const oldest = this.entries.keys().next()
      if (oldest.done) break
      this.delete(oldest.value)
    }
    const url = URL.createObjectURL(blob)
    this.entries.set(key, { id, url, size: blob.size })
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
    for (const [key, entry] of this.entries) if (!ids.has(entry.id)) this.delete(key)
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

/** Read authorized artifacts lazily, sharing cached images across discussion surfaces. */
export function useDiscussionThumbnailSource<T extends HTMLElement>(id: string, target?: string | null) {
  const cache = useContext(DiscussionThumbnailCacheContext)
  const ref = useRef<T>(null)
  const key = id + "\n" + (target ?? "")
  const [loaded, setLoaded] = useState<{ key: string; url: string } | null>(null)
  const source = cache?.get(key) ?? (loaded?.key === key ? loaded.url : null)
  useEffect(() => {
    const element = ref.current
    if (!element || !target || cache?.get(key)) return
    let started = false
    let temporaryUrl: string | null = null
    const abort = new AbortController()
    const load = async () => {
      if (started) return
      started = true
      const route = "/api/apps/nova/discussions/" + encodeURIComponent(id)
      try {
        if (!(target.startsWith("/api/assets/")
          || target.startsWith(route + "/messages/")) || target.includes("\\") || target.includes("..")) return
        const response = await novaExecution.fetch(target, { credentials: "include", signal: abort.signal })
        if (!response.ok || !response.headers.get("content-type")?.startsWith("image/")) return
        const blob = await response.blob()
        if (abort.signal.aborted || blob.size > imageLimit) return
        const url = cache ? cache.store(key, id, blob) : (temporaryUrl = URL.createObjectURL(blob))
        setLoaded({ key, url })
      } catch { /* An unavailable artifact keeps its icon. */ }
    }
    const observer = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { observer?.disconnect(); void load() }
    }) : null
    if (observer) observer.observe(element)
    else void load()
    return () => { abort.abort(); observer?.disconnect(); if (temporaryUrl) URL.revokeObjectURL(temporaryUrl) }
  }, [cache, id, key, target])
  return {
    ref, source,
    onError: () => { if (source) cache?.delete(key, source); setLoaded(null) },
  }
}

export function DiscussionThumbnail({ id, icon, target }: { id: string; icon: string; target?: string | null }) {
  const { ref, source, onError } = useDiscussionThumbnailSource<HTMLSpanElement>(id, target)
  return <span ref={ref} className="absolute inset-0 flex items-center justify-center overflow-hidden rounded-lg" aria-hidden="true">
    {source ? <img src={source} alt="" className="h-full w-full object-cover" onError={onError} />
      : <i className={"ph ph-" + groupingIcon(icon) + " text-base text-text-muted"} />}
  </span>
}
