import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
} from "@redbamboo/ui"
import type { AgentInfo } from "@/lib/types"

interface AgentPickerProps {
  agents: AgentInfo[]
  selectedId: string | null
  onSelect: (agentId: string | null) => void
  showAll?: boolean
}

export function AgentPicker({ agents, selectedId, onSelect, showAll = false }: AgentPickerProps) {
  const selected = agents.find((a) => a.id === selectedId)

  if (agents.length <= 1 && !showAll) return null

  return (
    <DropdownMenu>
      <DropdownMenuTrigger className="flex items-center gap-1.5 px-2 py-1 rounded-md text-xs font-medium text-text-muted hover:text-text hover:bg-overlay-5 transition-colors cursor-pointer">
        {selected ? (
          <>
            <AgentAvatar agent={selected} size={16} />
            <span>{selected.name}</span>
          </>
        ) : (
          <>
            <i className="fa-solid fa-users text-[10px]" />
            <span>All agents</span>
          </>
        )}
        <i className="fa-solid fa-chevron-down text-[8px] opacity-50" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" sideOffset={4}>
        {showAll && (
          <DropdownMenuItem
            onClick={() => onSelect(null)}
            className={!selectedId ? "text-primary" : ""}
          >
            <i className="fa-solid fa-users size-4 text-center" />
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
        const fallback = document.createElement("span")
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
