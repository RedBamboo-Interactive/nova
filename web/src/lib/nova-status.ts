import { getEffectiveToolName, isEventBlock } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"

export interface NovaStreamingStatus {
  icon: string
  label: string
}

const respondingStatus: NovaStreamingStatus = {
  icon: "ph-bold ph-chat-circle",
  label: "Responding...",
}

const toolLabels: Record<string, NovaStreamingStatus> = {
  Read:       { icon: "ph-bold ph-file-text",       label: "Reading..." },
  Write:      { icon: "ph-bold ph-pen",              label: "Writing..." },
  Edit:       { icon: "ph-bold ph-note-pencil",      label: "Editing..." },
  Glob:       { icon: "ph-bold ph-folder-open",      label: "Browsing files..." },
  Grep:       { icon: "ph-bold ph-magnifying-glass", label: "Searching..." },
  Bash:       { icon: "ph-bold ph-terminal",         label: "Running command..." },
  PowerShell: { icon: "ph-bold ph-terminal",         label: "Running command..." },
  WebFetch:   { icon: "ph-bold ph-globe",            label: "Fetching..." },
  WebSearch:  { icon: "ph-bold ph-globe",            label: "Searching the web..." },
  TodoWrite:  { icon: "ph-bold ph-list-checks",      label: "Planning..." },
}

/** Resolve the most specific live activity, with a prompt fallback before output begins. */
export function getNovaStreamingStatus(messages: MessageBlock[]): NovaStreamingStatus {
  for (let i = messages.length - 1; i >= 0; i--) {
    const block = messages[i]!
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

      if (part.type === "text") return respondingStatus
    }
  }

  return respondingStatus
}
