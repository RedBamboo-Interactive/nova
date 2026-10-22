import { useMemo } from "react"
import { ChatStatusLine, StreamingStatusLine, getSpinnerColor, isContextCompactionActive } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"
import { getNovaStreamingStatus } from "../lib/nova-status"

/**
 * The session's own status while it works. Built on ChatStatusLine so the
 * chat's activity row shows its shapes (agents, tasks, delegations) in it.
 */
export function NovaStatusLine({ isStreaming, isReconnecting = false, messages }: {
  isStreaming: boolean
  isReconnecting?: boolean
  messages: MessageBlock[]
}) {
  const spinnerColor = useMemo(() => getSpinnerColor(messages), [messages])
  const status = useMemo(() => getNovaStreamingStatus(messages), [messages])
  const isCompacting = useMemo(() => isContextCompactionActive(messages), [messages])

  if (isReconnecting) {
    return <StreamingStatusLine isStreaming={isStreaming} isReconnecting messages={messages} />
  }

  if (!isStreaming) return null

  if (isCompacting) {
    return <ChatStatusLine color="var(--color-text-disabled)" label="Compacting context..." />
  }

  return (
    <ChatStatusLine color={spinnerColor} icon={status.icon} label={status.label} />
  )
}
