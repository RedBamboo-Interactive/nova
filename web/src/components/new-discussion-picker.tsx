import { useState, useEffect, useRef } from "react"
import { api } from "../lib/api"
import type { AgentInfo } from "../lib/types"
import { getSettings, setSettings } from "../lib/settings-store"
import {
  getInitialAgentIndex,
  orderAgentsByName,
  reconcileHighlightedAgentId,
} from "../lib/new-discussion-picker"
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  Icon,
} from "@redbamboo/ui"

interface QualityTierInfo { slug: string; label: string; color?: string; icon?: string; isDefault: boolean }
interface ProviderInfo {
  slug: string
  name: string
  backend: string
  icon?: string
  iconSvgPath?: string
  color?: string
  isDefault: boolean
  defaultModel?: string
  hasApiKey: boolean
  description?: string
}

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
  const [highlightedAgentId, setHighlightedAgentId] = useState<string | null>(
    () => getSettings().lastUsedAgentId,
  )
  const [qualityTier, setQualityTier] = useState("")
  const [tiers, setTiers] = useState<QualityTierInfo[]>([])
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const [selectedProvider, setSelectedProvider] = useState<string | undefined>()
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) {
      setStarting(false)
      setFilter("")
      setHighlightedAgentId(getSettings().lastUsedAgentId)
      setQualityTier("")
      return
    }
    setLoading(agents.length === 0)
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
      .then(agentList => {
        const orderedAgents = orderAgentsByName(agentList)
        const initialIndex = getInitialAgentIndex(orderedAgents, getSettings().lastUsedAgentId)
        const initialAgent = orderedAgents[initialIndex]
        setAgents(orderedAgents)
        setHighlightedAgentId(currentId => reconcileHighlightedAgentId(
          orderedAgents,
          currentId,
          initialAgent?.id ?? null,
        ))
        setLoading(false)
        return orderedAgents
      })

    Promise.all([agentsP, providersP, tiersP]).then(([orderedAgents, provList, tierList]) => {
      const initialIndex = getInitialAgentIndex(orderedAgents, getSettings().lastUsedAgentId)
      const initialAgent = orderedAgents[initialIndex]
      if (initialAgent?.qualityTier && tierList.some(t => t.slug === initialAgent.qualityTier))
        setQualityTier(initialAgent.qualityTier)
      else
        setQualityTier(tierList.find(t => t.isDefault)?.slug ?? tierList[0]?.slug ?? "")
      if (initialAgent?.provider && provList.some(p => p.slug === initialAgent.provider))
        setSelectedProvider(initialAgent.provider)
      else {
        const def = provList.find(p => p.isDefault)
        if (def) setSelectedProvider(def.slug)
      }
    })
  }, [open])

  const filtered = agents.filter(a =>
    a.name.toLowerCase().includes(filter.toLowerCase())
  )

  const highlightedIndex = filtered.findIndex(agent => agent.id === highlightedAgentId)
  const highlighted = highlightedIndex >= 0 ? highlightedIndex : 0
  const highlightedAgent = filtered[highlighted]
  useEffect(() => {
    if (highlightedAgent) applyAgentDefaults(highlightedAgent)
  }, [highlightedAgent?.id])

  useEffect(() => {
    if (!open || filtered.length === 0) return
    listRef.current?.children[highlighted]?.scrollIntoView({ block: "nearest" })
  }, [open, filter, highlighted, filtered.length])

  function applyAgentDefaults(agent: AgentInfo) {
    if (agent.qualityTier && tiers.some(t => t.slug === agent.qualityTier))
      setQualityTier(agent.qualityTier)
    if (agent.provider && providers.some(p => p.slug === agent.provider))
      setSelectedProvider(agent.provider)
  }

  if (!open) return null

  const selectAgent = (agentId: string) => {
    setStarting(true)
    setHighlightedAgentId(agentId)
    setSettings({ lastUsedAgentId: agentId })
    onSelect(agentId, qualityTier, selectedProvider)
    onClose()
  }

  const onInputKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault()
      const next = Math.min(highlighted + 1, filtered.length - 1)
      listRef.current?.children[next]?.scrollIntoView({ block: "nearest" })
      setHighlightedAgentId(filtered[next]?.id ?? null)
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      const next = Math.max(highlighted - 1, 0)
      listRef.current?.children[next]?.scrollIntoView({ block: "nearest" })
      setHighlightedAgentId(filtered[next]?.id ?? null)
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

  const selectedTier = tiers.find(t => t.slug === qualityTier)
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
              onChange={e => {
                const value = e.target.value
                setFilter(value)
                const firstMatch = agents.find(agent =>
                  agent.name.toLowerCase().includes(value.toLowerCase()))
                setHighlightedAgentId(firstMatch?.id ?? null)
              }}
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
              onClick={() => {
                if (highlightedAgent?.id === agent.id && !starting) selectAgent(agent.id)
                else setHighlightedAgentId(agent.id)
              }}
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
                  {!agent.workspaceId && (
                    <p className="text-[11px] text-text-muted/70 truncate">
                      Disposable storage · no cross-discussion memory
                    </p>
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
                  <Icon
                    name={selectedProv?.icon}
                    svgPath={selectedProv?.iconSvgPath}
                    className="text-[10px]"
                    style={{ color: selectedProv?.color }}
                  />
                  <span>{selectedProv?.name ?? "Provider"}</span>
                  <Icon name="ph-bold ph-caret-down" className="text-[8px] opacity-50" />
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" sideOffset={4}>
                  {providers.map(p => (
                    <DropdownMenuItem
                      key={p.slug}
                      onClick={() => setSelectedProvider(p.slug)}
                      className={selectedProvider === p.slug ? "text-primary" : ""}
                    >
                      <Icon
                        name={p.icon}
                        svgPath={p.iconSvgPath}
                        className="size-4 text-center"
                        style={{ color: p.color }}
                      />
                      {p.name}
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            )}
            {selectedTier && <DropdownMenu>
              <DropdownMenuTrigger className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium text-text-muted hover:text-text hover:bg-overlay-5 transition-colors cursor-pointer">
                <Icon name={selectedTier.icon} className="text-[10px]" style={{ color: selectedTier.color }} />
                <span>{selectedTier.label}</span>
                <Icon name="ph-bold ph-caret-down" className="text-[8px] opacity-50" />
              </DropdownMenuTrigger>
              <DropdownMenuContent align="start" sideOffset={4}>
                {tiers.map(tier => (
                  <DropdownMenuItem
                    key={tier.slug}
                    onClick={() => setQualityTier(tier.slug)}
                    className={qualityTier === tier.slug ? "text-primary" : ""}
                  >
                    <Icon name={tier.icon} className="size-4 text-center" style={{ color: tier.color }} />
                    {tier.label}
                  </DropdownMenuItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>}
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={onClose}
              className="px-3 py-1.5 text-sm text-text-muted hover:text-contrast transition-colors"
            >
              Cancel
            </button>
            <button
              disabled={starting || filtered.length === 0 || !selectedTier}
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
