export interface DiscussionInfo {
  id: string
  title: string | null
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

export interface DiscussionSendResponse {
  text: string
  discussionId: string
  title: string | null
  toolCalls?: { name: string; input?: string; output?: string }[]
}
