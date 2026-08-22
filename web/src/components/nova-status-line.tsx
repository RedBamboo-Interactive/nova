import { useMemo } from "react"
import { ChatStatusLine, StreamingStatusLine, getSpinnerColor } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"
import { getNovaStreamingStatus } from "../lib/nova-status"

export function NovaStatusLine({ isStreaming, isReconnecting = false, messages }: {
  isStreaming: boolean
  isReconnecting?: boolean
  messages: MessageBlock[]
}) {
  const spinnerColor = useMemo(() => getSpinnerColor(messages), [messages])
  const status = useMemo(() => getNovaStreamingStatus(messages), [messages])

  if (isReconnecting) {
    return <StreamingStatusLine isStreaming={isStreaming} isReconnecting messages={messages} />
  }

  if (!isStreaming) return null

  return (
    <ChatStatusLine color={spinnerColor} icon={status.icon} label={status.label} />
  )
}
