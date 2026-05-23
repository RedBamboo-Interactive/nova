export interface DiscussionInfo {
  id: string
  title: string | null
  sessionId: string | null
  status: "idle" | "thinking" | "archived"
  createdAt: string
  lastActivity: string
  messageCount: number
}

export interface DiscussionMessage {
  id: string
  role: "user" | "assistant"
  parts: MessagePartDto[]
  timestamp: string
}

export interface MessagePartDto {
  type: "text" | "tool_use" | "tool_result"
  content: string
  toolName?: string
  toolInput?: string
}

export interface ClaudeStreamEvent {
  type: string
  content?: string | null
  toolName?: string | null
  toolInput?: unknown
  toolResult?: string | null
  messageId?: string | null
}

export interface WsEvent {
  type: string
  data: unknown
}
