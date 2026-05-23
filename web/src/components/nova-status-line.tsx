import { useMemo } from "react"
import { MorphSpinner, getSpinnerColor } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"

const toolLabels: Record<string, { icon: string; label: string }> = {
  Read:       { icon: "fa-solid fa-file-lines",       label: "Reading..." },
  Write:      { icon: "fa-solid fa-pen",              label: "Writing..." },
  Edit:       { icon: "fa-solid fa-pen-to-square",    label: "Editing..." },
  Glob:       { icon: "fa-solid fa-folder-open",      label: "Browsing files..." },
  Grep:       { icon: "fa-solid fa-magnifying-glass", label: "Searching..." },
  Bash:       { icon: "fa-solid fa-terminal",         label: "Running command..." },
  PowerShell: { icon: "fa-solid fa-terminal",         label: "Running command..." },
  WebFetch:   { icon: "fa-solid fa-globe",            label: "Fetching..." },
  WebSearch:  { icon: "fa-solid fa-globe",            label: "Searching the web..." },
  TodoWrite:  { icon: "fa-solid fa-list-check",       label: "Planning..." },
}

function getStatusFromMessages(messages: MessageBlock[]): { icon: string; label: string } | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    const block = messages[i]!
    if (block.role !== "assistant") continue

    for (let j = block.parts.length - 1; j >= 0; j--) {
      const part = block.parts[j]!

      if (part.type === "thinking") {
        return { icon: "fa-solid fa-brain", label: "Thinking..." }
      }

      if (part.type === "tool_use" && part.toolName) {
        const memoryPath = part.toolInput?.includes("memory/") || part.toolInput?.includes("memory\\")
        if (memoryPath && (part.toolName === "Read" || part.toolName === "Glob" || part.toolName === "Grep")) {
          return { icon: "fa-solid fa-brain", label: "Remembering..." }
        }
        if (memoryPath && (part.toolName === "Write" || part.toolName === "Edit")) {
          return { icon: "fa-solid fa-brain", label: "Memorizing..." }
        }

        const schedulePath = part.toolInput?.includes("schedule") || part.toolInput?.includes("heartbeat") || part.toolInput?.includes("cron")
        if (schedulePath) {
          return { icon: "fa-solid fa-clock", label: "Scheduling..." }
        }

        const known = toolLabels[part.toolName]
        if (known) return known

        return { icon: "fa-solid fa-gear", label: "Working..." }
      }

      if (part.type === "text") {
        return { icon: "fa-solid fa-comment", label: "Responding..." }
      }
    }
  }

  return null
}

export function NovaStatusLine({ isStreaming, messages }: {
  isStreaming: boolean
  messages: MessageBlock[]
}) {
  const spinnerColor = useMemo(() => getSpinnerColor(messages), [messages])
  const status = useMemo(() => getStatusFromMessages(messages), [messages])

  if (!isStreaming || !status) return null

  return (
    <div className="flex items-center gap-2.5 text-text-muted text-sm py-1">
      <MorphSpinner color={spinnerColor} />
      <i className={`${status.icon} text-[10px] opacity-60`} />
      <span>{status.label}</span>
    </div>
  )
}
