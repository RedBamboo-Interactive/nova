import { useState, useEffect } from "react"
import { ModalBase, ModalHeader } from "@redbamboo/ui"
import { api } from "../lib/api"

interface OutfitEntry {
  id: string
  name: string | null
  url: string
  prompt: string | null
  nsfw: boolean
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
  return `/api/assets/${filename}`
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
  const [showNsfw, setShowNsfw] = useState(false)

  useEffect(() => {
    const qs = agentId ? `?agentId=${agentId}` : ""
    api.get<OutfitData>(`/api/apps/nova/outfits${qs}`).then(setData).catch(() => {}).finally(() => setLoading(false))
  }, [])

  async function selectOutfit(outfit: OutfitEntry | null) {
    const key = outfit?.url ?? "__base__"
    setSelecting(key)
    try {
      await api.post("/api/apps/nova/outfits/select", {
        url: outfit?.url ?? "",
        outfitId: outfit?.id ?? null,
        discussionId,
      })
      setData(prev => {
        if (!prev) return prev
        return {
          ...prev,
          currentOverride: outfit?.id ?? null,
          outfits: prev.outfits.map(o => ({ ...o, active: o.id === outfit?.id })),
        }
      })
      window.dispatchEvent(new Event("nova:avatar-changed"))
    } catch { /* ignore */ }
    finally { setSelecting(null) }
  }

  const isBaseActive = !data?.outfits.some(o => o.active)

  const filteredOutfits = data?.outfits.filter(o => o.active || showNsfw || !o.nsfw) ?? []
  const nsfwCount = data?.outfits.filter(o => o.nsfw).length ?? 0

  return (
    <ModalBase dataModal="outfit-browser" ariaLabel="Browse outfits" onClose={onClose} size="lg">
      <ModalHeader
        icon={<i className="ph-bold ph-t-shirt text-primary" />}
        title={<span className="text-sm font-medium">Outfits</span>}
        onClose={onClose}
      />
      <div className="px-6 pb-5">
        {loading ? (
          <div className="flex items-center justify-center py-8 text-text-muted text-sm">
            <i className="ph-bold ph-spinner animate-spin mr-2" /> Loading...
          </div>
        ) : (
          <>
            {nsfwCount > 0 && (
              <div className="flex items-center gap-2 mb-3">
                <button
                  onClick={() => setShowNsfw(v => !v)}
                  className={`flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs font-medium transition-colors ${
                    showNsfw
                      ? "bg-rose-500/20 text-rose-400 hover:bg-rose-500/30"
                      : "bg-overlay-6 text-text-muted hover:bg-overlay-10"
                  }`}
                >
                  <i className={`ph-bold ${showNsfw ? "ph-eye" : "ph-eye-slash"} text-[10px]`} />
                  Show NSFW
                  <span className="text-[10px] opacity-60">({nsfwCount})</span>
                </button>
              </div>
            )}
            <div className="grid grid-cols-3 sm:grid-cols-4 gap-3">
              {/* Base avatar — reset option */}
              <button
                onClick={() => selectOutfit(null)}
                className={`group relative aspect-[3/4] rounded-lg overflow-hidden border-2 transition-all ${
                  isBaseActive ? "border-primary ring-2 ring-primary/30" : "border-overlay-6 hover:border-overlay-10"
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
                    <i className="ph-bold ph-spinner animate-spin text-white" />
                  </div>
                )}
                {isBaseActive && (
                  <div className="absolute top-1.5 right-1.5 w-5 h-5 rounded-full bg-primary flex items-center justify-center">
                    <i className="ph-bold ph-check text-[10px] text-white" />
                  </div>
                )}
              </button>

              {/* Outfit history */}
              {filteredOutfits.map((outfit) => {
                return (
                  <button
                    key={outfit.id}
                    onClick={() => selectOutfit(outfit)}
                    className={`group relative aspect-[3/4] rounded-lg overflow-hidden border-2 transition-all ${
                      outfit.active ? "border-primary ring-2 ring-primary/30" : "border-overlay-6 hover:border-overlay-10"
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
                    {outfit.nsfw && (
                      <div className="absolute top-1.5 left-1.5 px-1.5 py-0.5 rounded bg-rose-500/80 text-[8px] text-white font-bold">
                        NSFW
                      </div>
                    )}
                    {selecting === outfit.url && (
                      <div className="absolute inset-0 bg-black/40 flex items-center justify-center">
                        <i className="ph-bold ph-spinner animate-spin text-white" />
                      </div>
                    )}
                    {outfit.active && (
                      <div className="absolute top-1.5 right-1.5 w-5 h-5 rounded-full bg-primary flex items-center justify-center">
                        <i className="ph-bold ph-check text-[10px] text-white" />
                      </div>
                    )}
                  </button>
                )
              })}

              {(!filteredOutfits.length) && (
                <div className="col-span-2 sm:col-span-3 flex items-center justify-center py-8 text-text-muted text-xs">
                  {nsfwCount > 0 && !showNsfw
                    ? "All outfits are NSFW. Toggle the filter to see them."
                    : "No outfit changes yet. Nova will start generating them daily."}
                </div>
              )}
            </div>
          </>
        )}
      </div>
    </ModalBase>
  )
}
