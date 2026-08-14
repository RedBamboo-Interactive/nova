import { useMemo } from "react"
import { MorphSpinner, getSpinnerColor } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"
import { getNovaStreamingStatus } from "../lib/nova-status"

export function NovaStatusLine({ isStreaming, messages }: {
  isStreaming: boolean
  messages: MessageBlock[]
}) {
  const spinnerColor = useMemo(() => getSpinnerColor(messages), [messages])
  const status = useMemo(() => getNovaStreamingStatus(messages), [messages])

  if (!isStreaming) return null

  return (
    <div className="flex items-center gap-2.5 text-text-muted text-sm py-1">
      <MorphSpinner color={spinnerColor} />
      <i className={`${status.icon} text-[10px] opacity-60`} />
      <span>{status.label}</span>
    </div>
  )
}
