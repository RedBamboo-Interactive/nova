import { useEffect, useRef, useState } from "react"
import { api, novaExecution } from "../../lib/api"
import { groupingIcon } from "../../lib/discussion-grouping"

/** One visible thumbnail at a time; only existing authorized same-origin artifact routes. */
export function DiscussionThumbnail({ id, icon }: { id: string; icon: string }) {
  const ref = useRef<HTMLSpanElement>(null)
  const [source, setSource] = useState<string | null>(null)
  useEffect(() => {
    const element = ref.current
    if (!element) return
    let disposed = false
    let started = false
    let objectUrl: string | null = null
    const abort = new AbortController()
    const load = async () => {
      if (started) return
      started = true
      try {
        const page = await api.get<{ items: Array<{ kind: string; target: string }> }>(`/api/apps/nova/discussions/${encodeURIComponent(id)}/artifacts?kinds=image&limit=1`)
        const target = page.items.find(item => item.kind === "image")?.target
        if (disposed || !target || !(target.startsWith("/api/assets/")
          || target.startsWith(`/api/apps/nova/discussions/${encodeURIComponent(id)}/messages/`)) || target.includes("\\") || target.includes("..")) return
        const response = await novaExecution.fetch(target, { credentials: "include", signal: abort.signal })
        if (!response.ok || !response.headers.get("content-type")?.startsWith("image/")) return
        const blob = await response.blob()
        if (disposed || blob.size > 10 * 1024 * 1024) return
        objectUrl = URL.createObjectURL(blob)
        setSource(objectUrl)
      } catch { /* An unavailable artifact keeps its icon. */ }
    }
    const observer = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { observer?.disconnect(); void load() }
    }) : null
    if (observer) observer.observe(element)
    else void load()
    return () => { disposed = true; abort.abort(); observer?.disconnect(); if (objectUrl) URL.revokeObjectURL(objectUrl) }
  }, [id])
  return <span ref={ref} className="absolute inset-0 flex items-center justify-center overflow-hidden rounded-lg" aria-hidden="true">
    {source ? <img src={source} alt="" className="h-full w-full object-cover" onError={() => setSource(null)} />
      : <i className={`ph ph-${groupingIcon(icon)} text-base text-text-muted`} />}
  </span>
}

