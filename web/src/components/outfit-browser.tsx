import { useState, useEffect } from "react"
import { ModalBase, ModalHeader } from "@redbamboo/ui"
import { api } from "@/lib/api"

interface OutfitEntry {
  id: string
  name: string | null
  url: string
  prompt: string | null
  date: string | null
  active: boolean
}

interface OutfitData {
  baseAvatarUrl: string
  currentOverride: string | null
  outfits: OutfitEntry[]
}

interface Props {
  onClose: () => void
  discussionId?: string | null
  agentId?: string | null
}

function assetSrc(relativeUrl: string): string {
  if (!relativeUrl) return "/nova-avatar.png"
  const filename = relativeUrl.split("/").pop() ?? relativeUrl
  return `/api/redleaf-asset/${filename}`
}

function relativeDate(iso: string | null): string {
  if (!iso) return ""
  const d = new Date(iso)
  const now = new Date()
  const diff = now.getTime() - d.getTime()
  const mins = Math.floor(diff / 60000)
  if (mins < 60) return `${mins}m ago`
  const hrs = Math.floor(mins / 60)
  if (hrs < 24) return `${hrs}h ago`
  const days = Math.floor(hrs / 24)
  return `${days}d ago`
}

export function OutfitBrowser({ onClose, discussionId, agentId }: Props) {
  const [data, setData] = useState<OutfitData | null>(null)
  const [loading, setLoading] = useState(true)
  const [selecting, setSelecting] = useState<string | null>(null)

  useEffect(() => {
    const qs = agentId ? `?agentId=${agentId}` : ""
    api.get<OutfitData>(`/api/outfits${qs}`).then(setData).catch(() => {}).finally(() => setLoading(false))
  }, [])

  async function selectOutfit(outfit: OutfitEntry | null) {
    const key = outfit?.url ?? "__base__"
    setSelecting(key)
    try {
      await api.post("/api/outfits/select", {
        url: outfit?.url ?? "",
        outfitId: outfit?.id ?? null,
        discussionId,
      })
      setData(prev => prev ? { ...prev, currentOverride: outfit?.url ?? null } : prev)
      window.dispatchEvent(new Event("nova:avatar-changed"))
    } catch { /* ignore */ }
    finally { setSelecting(null) }
  }

  const isActive = (url: string | null) => {
    if (!url) return !data?.currentOverride
    if (!data?.currentOverride) return false
    const overrideFile = data.currentOverride.split("/").pop()
    const urlFile = url.split("/").pop()
    return overrideFile === urlFile
  }

  return (
    <ModalBase dataModal="outfit-browser" ariaLabel="Browse outfits" onClose={onClose} size="lg">
      <ModalHeader
        icon={<i className="fa-solid fa-shirt text-primary" />}
        title={<span className="text-sm font-medium">Outfits</span>}
        onClose={onClose}
      />
      <div className="px-6 pb-5">
        {loading ? (
          <div className="flex items-center justify-center py-8 text-text-muted text-sm">
            <i className="fa-solid fa-spinner fa-spin mr-2" /> Loading...
          </div>
        ) : (
          <div className="grid grid-cols-3 sm:grid-cols-4 gap-3">
            {/* Base avatar — reset option */}
            <button
              onClick={() => selectOutfit(null)}
              className={`group relative aspect-[3/4] rounded-lg overflow-hidden border-2 transition-all ${
                isActive(null) ? "border-primary ring-2 ring-primary/30" : "border-overlay-6 hover:border-overlay-10"
              }`}
            >
              <img
                src={data?.baseAvatarUrl ?? "/nova-avatar.png"}
                alt="Base"
                className="w-full h-full object-cover object-top"
              />
              <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/70 to-transparent p-2">
                <span className="text-[10px] text-white font-medium">Base</span>
              </div>
              {selecting === "__base__" && (
                <div className="absolute inset-0 bg-black/40 flex items-center justify-center">
                  <i className="fa-solid fa-spinner fa-spin text-white" />
                </div>
              )}
              {isActive(null) && (
                <div className="absolute top-1.5 right-1.5 w-5 h-5 rounded-full bg-primary flex items-center justify-center">
                  <i className="fa-solid fa-check text-[10px] text-white" />
                </div>
              )}
            </button>

            {/* Outfit history */}
            {data?.outfits.map((outfit) => {
              return (
                <button
                  key={outfit.id}
                  onClick={() => selectOutfit(outfit)}
                  className={`group relative aspect-[3/4] rounded-lg overflow-hidden border-2 transition-all ${
                    isActive(outfit.url) ? "border-primary ring-2 ring-primary/30" : "border-overlay-6 hover:border-overlay-10"
                  }`}
                  title={outfit.prompt ?? undefined}
                >
                  <img
                    src={assetSrc(outfit.url)}
                    alt={outfit.prompt ?? "Outfit"}
                    className="w-full h-full object-cover object-top"
                    onError={e => { (e.target as HTMLImageElement).src = "/nova-avatar.png" }}
                  />
                  <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/70 to-transparent p-2">
                    <div className="text-[10px] text-white font-medium truncate">{outfit.name ?? "Outfit"}</div>
                    <div className="text-[9px] text-white/60">{relativeDate(outfit.date)}</div>
                  </div>
                  {selecting === outfit.url && (
                    <div className="absolute inset-0 bg-black/40 flex items-center justify-center">
                      <i className="fa-solid fa-spinner fa-spin text-white" />
                    </div>
                  )}
                  {isActive(outfit.url) && (
                    <div className="absolute top-1.5 right-1.5 w-5 h-5 rounded-full bg-primary flex items-center justify-center">
                      <i className="fa-solid fa-check text-[10px] text-white" />
                    </div>
                  )}
                </button>
              )
            })}

            {(!data?.outfits.length) && (
              <div className="col-span-2 sm:col-span-3 flex items-center justify-center py-8 text-text-muted text-xs">
                No outfit changes yet. Nova will start generating them daily.
              </div>
            )}
          </div>
        )}
      </div>
    </ModalBase>
  )
}
