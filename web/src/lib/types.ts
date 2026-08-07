export interface DiscussionInfo {
  id: string
  entityId: string
  title: string | null
  sessionId: string | null
  /** "archiving" = archive intent committed server-side, session stop not yet
   * confirmed. Treated exactly like "archived" everywhere in the UI. */
  status: "idle" | "thinking" | "stopped" | "archiving" | "archived"
  /** "heartbeat" = the agent's standing background-participant discussion:
   * LIVE-rendered, no chat input, no unread/badge accounting. */
  type: "chat" | "live" | "heartbeat"
  createdAt: string
  lastActivity: string
  messageCount: number
  lastReadAt: string | null
  agentId: string | null
  qualityTier?: string | null
  provider?: string | null
  confidential?: boolean
}

export interface AgentInfo {
  id: string
  slug: string
  name: string
  description: string | null
  avatarUrl: string
  workspaceId?: string | null
  provider?: string | null
  qualityTier?: string | null
}

export interface DiscussionMessage {
  id: string
  /** Universal message identity (shared with the session-transcript copy of
   * the same message). Preferred over id as the block id when present. */
  messageUid?: string | null
  role: "user" | "assistant"
  parts: MessagePartDto[]
  timestamp: string
  senderAgentId?: string
  source?: string
}

export interface EventType {
  key: string
  name: string
  icon: string | null
  color: string | null
  description: string | null
}

export interface MessagePartDto {
  type: "text" | "tool_use" | "tool_result" | "audio" | "event_data" | "image"
  content: string
  toolName?: string
  toolInput?: string
  url?: string
  base64?: string
  mediaType?: string
  attachments?: import("@redbamboo/chat").UploadedAttachment[]
}

export interface ClaudeStreamEvent {
  /**
   * Open-ended on purpose: the backend keeps adding event kinds (most recently
   * the "question" / "question_resolved" control pair) and a narrower union
   * here would only push the drop one layer down, into the mapping in
   * use-discussions.
   */
  type: string
  content?: string | null
  toolName?: string | null
  toolInput?: unknown
  toolResult?: string | null
  payloadRef?: import("@redbamboo/chat").TranscriptPayloadRef | null
  messageId?: string | null
  messageUid?: string | null
  /**
   * Correlation id for a tool call the session is parked on, carried by
   * "question" and echoed by "question_resolved". Live-only — it is not
   * persisted, so a reloaded conversation never has one.
   */
  requestId?: string | null
}

export interface WsEvent {
  type: string
  data: unknown
}
