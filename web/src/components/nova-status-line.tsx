import { useMemo } from "react"
import { MorphSpinner, getEffectiveToolName, getSpinnerColor, isEventBlock } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"

const toolLabels: Record<string, { icon: string; label: string }> = {
  Read:       { icon: "ph-bold ph-file-text",       label: "Reading..." },
  Write:      { icon: "ph-bold ph-pen",              label: "Writing..." },
  Edit:       { icon: "ph-bold ph-note-pencil",    label: "Editing..." },
  Glob:       { icon: "ph-bold ph-folder-open",      label: "Browsing files..." },
  Grep:       { icon: "ph-bold ph-magnifying-glass", label: "Searching..." },
  Bash:       { icon: "ph-bold ph-terminal",         label: "Running command..." },
  PowerShell: { icon: "ph-bold ph-terminal",         label: "Running command..." },
  WebFetch:   { icon: "ph-bold ph-globe",            label: "Fetching..." },
  WebSearch:  { icon: "ph-bold ph-globe",            label: "Searching the web..." },
  TodoWrite:  { icon: "ph-bold ph-list-checks",       label: "Planning..." },
}

function getStatusFromMessages(messages: MessageBlock[]): { icon: string; label: string } | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    const block = messages[i]!
    // Ambient event groups trail the in-flight block and would otherwise match
    // the generic tool_use branch below, reporting "Working..." for a weather
    // update instead of what Nova is actually doing.
    if (block.role !== "assistant" || isEventBlock(block)) continue

    for (let j = block.parts.length - 1; j >= 0; j--) {
      const part = block.parts[j]!

      if (part.type === "thinking") {
        return { icon: "ph-bold ph-brain", label: "Thinking..." }
      }

      if (part.type === "tool_use" && part.toolName) {
        const effectiveName = getEffectiveToolName(part.toolName, part.toolInput)
        const memoryPath = part.toolInput?.includes("memory/") || part.toolInput?.includes("memory\\")
        if (memoryPath && (effectiveName === "Read" || effectiveName === "Glob" || effectiveName === "Grep")) {
          return { icon: "ph-bold ph-brain", label: "Remembering..." }
        }
        if (memoryPath && (effectiveName === "Write" || effectiveName === "Edit")) {
          return { icon: "ph-bold ph-brain", label: "Memorizing..." }
        }

        const automationPath = part.toolInput?.includes("automation") || part.toolInput?.includes("schedule") || part.toolInput?.includes("cron")
        if (automationPath) {
          return { icon: "ph-bold ph-lightning", label: "Automating..." }
        }

        const known = effectiveName ? toolLabels[effectiveName] : undefined
        if (known) return known

        return { icon: "ph-bold ph-gear", label: "Working..." }
      }

      if (part.type === "text") {
        return { icon: "ph-bold ph-chat-circle", label: "Responding..." }
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
