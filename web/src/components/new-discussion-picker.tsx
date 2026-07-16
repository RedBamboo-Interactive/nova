import { useState, useEffect, useRef } from "react"
import { api } from "../lib/api"
import type { AgentInfo } from "../lib/types"
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
} from "@redbamboo/ui"

interface QualityTierInfo { slug: string; label: string; color: string; icon: string }
interface ProviderInfo { slug: string; name: string; backend: string; icon?: string; isDefault: boolean; defaultModel?: string; hasApiKey: boolean; description?: string }

const FALLBACK_TIERS: QualityTierInfo[] = [
  { slug: "fast",     label: "Fast",     color: "#22d3ee", icon: "ph-bold ph-rabbit"     },
  { slug: "standard", label: "Standard", color: "#a78bfa", icon: "ph-bold ph-lightning"  },
  { slug: "deep",     label: "Deep",     color: "#fb923c", icon: "ph-bold ph-brain"      },
  { slug: "research", label: "Research", color: "#f43f5e", icon: "ph-bold ph-microscope" },
]

interface Props {
  open: boolean
  onClose: () => void
  onSelect: (agentId: string, qualityTier: string, provider?: string) => void
}

export function NewDiscussionPicker({ open, onClose, onSelect }: Props) {
  const [agents, setAgents] = useState<AgentInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [filter, setFilter] = useState("")
  const [starting, setStarting] = useState(false)
  const [highlighted, setHighlighted] = useState(0)
  const [qualityTier, setQualityTier] = useState("standard")
  const [tiers, setTiers] = useState<QualityTierInfo[]>(FALLBACK_TIERS)
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const [selectedProvider, setSelectedProvider] = useState<string | undefined>()
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) {
      setStarting(false)
      setFilter("")
      setHighlighted(0)
      setQualityTier("standard")
      return
    }
    setLoading(true)
    const tiersP = api.get<{ tiers: QualityTierInfo[] }>("/ai-session/quality-modes")
      .then(data => { if (data.tiers?.length) setTiers(data.tiers); return data.tiers ?? [] })
      .catch(() => [] as QualityTierInfo[])
    const providersP = api.get<ProviderInfo[]>("/ai-session/providers/configured")
      .then(data => {
        if (Array.isArray(data) && data.length > 0) {
          setProviders(data)
          return data
        }
        return [] as ProviderInfo[]
      })
      .catch(() => [] as ProviderInfo[])
    const agentsP = api.get<AgentInfo[]>("/api/apps/nova/agents")
      .catch(() => [] as AgentInfo[])

    Promise.all([agentsP, providersP, tiersP]).then(([agentList, provList]) => {
      setAgents(agentList)
      const first = agentList[0]
      if (first?.qualityMode) setQualityTier(first.qualityMode)
      if (first?.provider && provList.some(p => p.slug === first.provider))
        setSelectedProvider(first.provider)
      else {
        const def = provList.find(p => p.isDefault)
        if (def) setSelectedProvider(def.slug)
      }
      setLoading(false)
    })
  }, [open])

  const filtered = agents.filter(a =>
    a.name.toLowerCase().includes(filter.toLowerCase())
  )

  const highlightedAgent = filtered[highlighted]
  useEffect(() => {
    if (highlightedAgent) applyAgentDefaults(highlightedAgent)
  }, [highlightedAgent?.id])

  function applyAgentDefaults(agent: AgentInfo) {
    if (agent.qualityMode) setQualityTier(agent.qualityMode)
    if (agent.provider && providers.some(p => p.slug === agent.provider))
      setSelectedProvider(agent.provider)
  }

  if (!open) return null

  const selectAgent = (agentId: string) => {
    setStarting(true)
    onSelect(agentId, qualityTier, selectedProvider)
    onClose()
  }

  const onInputKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault()
      setHighlighted(i => {
        const next = Math.min(i + 1, filtered.length - 1)
        listRef.current?.children[next]?.scrollIntoView({ block: "nearest" })
        return next
      })
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setHighlighted(i => {
        const next = Math.max(i - 1, 0)
        listRef.current?.children[next]?.scrollIntoView({ block: "nearest" })
        return next
      })
    } else if (e.key === "Enter") {
      e.preventDefault()
      if (filtered.length > 0 && !starting) {
        selectAgent(filtered[highlighted]?.id ?? filtered[0]!.id)
      }
    } else if (e.key === "Escape") {
      e.preventDefault()
      onClose()
    }
  }

  const selectedTier = tiers.find(t => t.slug === qualityTier) ?? tiers[1]!
  const selectedProv = providers.find(p => p.slug === selectedProvider)

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-surface-deep border border-border-subtle rounded-xl w-full max-w-md mx-4 overflow-hidden shadow-xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="p-4 border-b border-border-subtle">
          <h2 className="text-base font-semibold mb-2">New Discussion</h2>
          {agents.length > 1 && (
            <input
              type="text"
              value={filter}
              onChange={e => { setFilter(e.target.value); setHighlighted(0) }}
              onKeyDown={onInputKeyDown}
              placeholder="Filter agents..."
              autoFocus
              className="w-full bg-overlay-5 border border-border-subtle rounded-lg px-3 py-2 text-sm placeholder:text-text-muted focus:border-overlay-30"
            />
          )}
        </div>
        <div ref={listRef} className="max-h-80 overflow-y-auto">
          {loading && <p className="p-4 text-sm text-text-muted">Loading...</p>}
          {!loading && filtered.length === 0 && (
            <p className="p-4 text-sm text-text-muted">No agents found</p>
          )}
          {filtered.map((agent, i) => (
            <button
              key={agent.id}
              disabled={starting}
              onClick={() => { if (highlighted === i && !starting) selectAgent(agent.id); else setHighlighted(i) }}
              onKeyDown={agents.length <= 1 ? onInputKeyDown : undefined}
              autoFocus={agents.length <= 1 && i === 0}
              className={`w-full text-left px-4 py-3 transition-colors border-b border-border-subtle last:border-b-0 ${
                i === highlighted ? "bg-overlay-10" : "hover:bg-overlay-5"
              }`}
            >
              <div className="flex items-center gap-3">
                <img
                  src={agent.avatarUrl}
                  alt=""
                  className="w-7 h-7 rounded-full object-cover flex-shrink-0"
                  onError={e => { e.currentTarget.style.display = "none" }}
                />
                <div className="min-w-0">
                  <span className="text-sm font-medium">{agent.name}</span>
                  {agent.description && (
                    <p className="text-xs text-text-muted truncate">{agent.description}</p>
                  )}
                </div>
              </div>
            </button>
          ))}
        </div>
        <div className="p-3 border-t border-border-subtle flex items-center justify-between">
          <div className="flex items-center gap-2">
            {providers.length > 0 && (
              <DropdownMenu>
                <DropdownMenuTrigger className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium text-text-muted hover:text-text hover:bg-overlay-5 transition-colors cursor-pointer">
                  <i className={(selectedProv?.icon ?? "ph-bold ph-plug") + " text-[10px]"} />
                  <span>{selectedProv?.name ?? "Provider"}</span>
                  <i className="ph-bold ph-caret-down text-[8px] opacity-50" />
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" sideOffset={4}>
                  {providers.map(p => (
                    <DropdownMenuItem
                      key={p.slug}
                      onClick={() => setSelectedProvider(p.slug)}
                      className={selectedProvider === p.slug ? "text-primary" : ""}
                    >
                      <i className={(p.icon ?? "ph-bold ph-plug") + " size-4 text-center"} />
                      {p.name}
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            )}
            <DropdownMenu>
              <DropdownMenuTrigger className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium text-text-muted hover:text-text hover:bg-overlay-5 transition-colors cursor-pointer">
                <i className={selectedTier.icon + " text-[10px]"} style={{ color: selectedTier.color }} />
                <span>{selectedTier.label}</span>
                <i className="ph-bold ph-caret-down text-[8px] opacity-50" />
              </DropdownMenuTrigger>
              <DropdownMenuContent align="start" sideOffset={4}>
                {tiers.map(tier => (
                  <DropdownMenuItem
                    key={tier.slug}
                    onClick={() => setQualityTier(tier.slug)}
                    className={qualityTier === tier.slug ? "text-primary" : ""}
                  >
                    <i className={tier.icon + " size-4 text-center"} style={{ color: tier.color }} />
                    {tier.label}
                  </DropdownMenuItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={onClose}
              className="px-3 py-1.5 text-sm text-text-muted hover:text-contrast transition-colors"
            >
              Cancel
            </button>
            <button
              disabled={starting || filtered.length === 0}
              onClick={() => { if (highlightedAgent) selectAgent(highlightedAgent.id) }}
              className="px-4 py-1.5 text-sm font-medium bg-primary text-primary-foreground rounded-md hover:bg-primary/90 transition-colors disabled:opacity-50"
            >
              Go
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
