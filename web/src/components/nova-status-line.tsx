import { useMemo } from "react"
import { ChatStatusLine, StreamingStatusLine, getSpinnerColor, isContextCompactionActive } from "@redbamboo/chat"
import type { MessageBlock } from "@redbamboo/chat"
import { getNovaStreamingStatus } from "../lib/nova-status"
import type { DiscussionDelegationActivity } from "../lib/types"
import { delegationActivityLabel, delegationAnimationPaused } from "../lib/delegation-activity"
import { DelegationSpinner } from "./delegation-activity"

export function NovaStatusLine({ isStreaming, isReconnecting = false, messages, delegationActivity, delegationsInBackgroundRow = false }: {
  isStreaming: boolean
  isReconnecting?: boolean
  messages: MessageBlock[]
  delegationActivity?: DiscussionDelegationActivity
  /** Delegations are shown in the shared background row (background tasks also run). */
  delegationsInBackgroundRow?: boolean
}) {
  const spinnerColor = useMemo(() => getSpinnerColor(messages), [messages])
  const status = useMemo(() => getNovaStreamingStatus(messages), [messages])
  const isCompacting = useMemo(() => isContextCompactionActive(messages), [messages])

  if (isReconnecting) {
    return <StreamingStatusLine isStreaming={isStreaming} isReconnecting messages={messages} />
  }

  if (!isStreaming) return !delegationsInBackgroundRow && delegationActivity && (delegationActivity.ongoingCount > 0 || delegationActivity.unknownCount > 0) ? (
    <div data-slot="delegation-status-line" className="flex items-center gap-2.5 text-text-muted text-sm py-1">
      <DelegationSpinner paused={delegationAnimationPaused(delegationActivity)} />
      <span>{delegationActivityLabel(delegationActivity)}</span>
    </div>
  ) : null

  if (isCompacting) {
    return <ChatStatusLine color="var(--color-text-disabled)" label="Compacting context..." />
  }

  return (
    <ChatStatusLine color={spinnerColor} icon={status.icon} label={status.label} />
  )
}
