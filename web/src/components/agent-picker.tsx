import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
} from "@redbamboo/ui"
import type { AgentInfo } from "../lib/types"

interface AgentPickerProps {
  agents: AgentInfo[]
  selectedId: string | null
  onSelect: (agentId: string | null) => void
  showAll?: boolean
  /**
   * Inside a `dock-header` container (a docked or wide inline header), the label collapses
   * to icon + chevron when the header is too narrow for it (the full label stays as
   * the accessible name and tooltip), and the trigger tightens at the narrowest
   * widths. The thresholds belong to the Discussions header's layout.
   */
  collapsible?: boolean
}

export function AgentPicker({ agents, selectedId, onSelect, showAll = false, collapsible = false }: AgentPickerProps) {
  const selected = agents.find((a) => a.id === selectedId)
  const label = selected ? selected.name : "All agents"

  if (agents.length <= 1 && !showAll) return null

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        className={`flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium text-text-muted hover:text-text hover:bg-overlay-5 transition-colors cursor-pointer${collapsible ? " @min-[0px]/dock-header:whitespace-nowrap @min-[0px]/dock-header:shrink-0 @max-[201px]/dock-header:px-1.5" : ""}`}
        aria-label={collapsible ? `Agent filter: ${label}` : undefined}
        title={collapsible ? label : undefined}
      >
        {selected ? (
          <>
            <AgentAvatar agent={selected} size={16} />
            {/* An agent name never takes more room than "All agents". */}
            <span className={collapsible ? "@min-[0px]/dock-header:max-w-[58px] @min-[0px]/dock-header:truncate @max-[294px]/dock-header:hidden" : undefined}>{selected.name}</span>
          </>
        ) : (
          <>
            <i className="ph-bold ph-users text-[10px]" />
            <span className={collapsible ? "@max-[294px]/dock-header:hidden" : undefined}>All agents</span>
          </>
        )}
        <i className="ph-bold ph-caret-down text-[8px] opacity-50" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" sideOffset={4}>
        {showAll && (
          <DropdownMenuItem
            onClick={() => onSelect(null)}
            className={!selectedId ? "text-primary" : ""}
          >
            <i className="ph-bold ph-users size-4 text-center" />
            All agents
          </DropdownMenuItem>
        )}
        {agents.map((agent) => (
          <DropdownMenuItem
            key={agent.id}
            onClick={() => onSelect(agent.id)}
            className={selectedId === agent.id ? "text-primary" : ""}
          >
            <AgentAvatar agent={agent} size={16} />
            {agent.name}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

interface AgentAvatarProps {
  agent: AgentInfo
  size?: number
  className?: string
}

export function AgentAvatar({ agent, size = 20, className = "" }: AgentAvatarProps) {
  return (
    <img
      src={agent.avatarUrl}
      alt={agent.name}
      className={`rounded-full object-cover shrink-0 ${className}`}
      style={{ width: size, height: size }}
      onError={(e) => {
        const el = e.currentTarget
        el.style.display = "none"
        const fallback = el.ownerDocument.createElement("span")
        fallback.className = `inline-flex items-center justify-center rounded-full bg-overlay-10 text-text-muted shrink-0 ${className}`
        fallback.style.width = `${size}px`
        fallback.style.height = `${size}px`
        fallback.style.fontSize = `${Math.round(size * 0.45)}px`
        fallback.textContent = agent.name[0] ?? "?"
        el.parentElement?.insertBefore(fallback, el)
      }}
    />
  )
}
