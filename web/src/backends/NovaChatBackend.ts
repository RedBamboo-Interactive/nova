import type { ChatBackend, ChatEvent, ImageAttachment } from "@redbamboo/chat"
import { api } from "@/lib/api"

interface ChatSendResponse {
  text: string
  contextId: string
  toolCalls?: { name: string; input?: string; output?: string }[]
}

export class NovaChatBackend implements ChatBackend {
  private contextId: string | null = null
  private listeners = new Map<string, Set<(event: ChatEvent) => void>>()

  async sendMessage(
    text: string,
    _images?: ImageAttachment[],
  ): Promise<{ sessionId: string }> {
    const sessionId = this.contextId ?? crypto.randomUUID().slice(0, 8)

    const subs = this.listeners.get(sessionId)

    const emit = (event: ChatEvent) => {
      subs?.forEach((cb) => cb(event))
    }

    emit({ type: "status", content: "thinking" })

    try {
      const response = await api.post<ChatSendResponse>("/api/chat/send", {
        message: text,
        contextId: sessionId,
      })

      this.contextId = response.contextId

      if (response.toolCalls) {
        for (const tc of response.toolCalls) {
          emit({
            type: "tool_use",
            toolName: tc.name,
            toolInput: tc.input ?? "",
          })
          if (tc.output) {
            emit({
              type: "tool_result",
              toolName: tc.name,
              toolResult: tc.output,
            })
          }
        }
      }

      emit({ type: "text", content: response.text })
      emit({ type: "status", content: "done" })
    } catch (err) {
      emit({
        type: "error",
        content: err instanceof Error ? err.message : "Unknown error",
      })
    }

    return { sessionId }
  }

  subscribe(
    sessionId: string,
    onEvent: (event: ChatEvent) => void,
  ): () => void {
    if (!this.listeners.has(sessionId)) {
      this.listeners.set(sessionId, new Set())
    }
    this.listeners.get(sessionId)!.add(onEvent)

    return () => {
      this.listeners.get(sessionId)?.delete(onEvent)
    }
  }

  async reset(): Promise<void> {
    this.contextId = null
    this.listeners.clear()
  }
}
