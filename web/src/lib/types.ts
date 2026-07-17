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
  status: string
  avatarUrl: string
  provider?: string | null
  qualityMode?: string | null
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
}

export interface ClaudeStreamEvent {
  type: string
  content?: string | null
  toolName?: string | null
  toolInput?: unknown
  toolResult?: string | null
  messageId?: string | null
  messageUid?: string | null
}

export interface WsEvent {
  type: string
  data: unknown
}
