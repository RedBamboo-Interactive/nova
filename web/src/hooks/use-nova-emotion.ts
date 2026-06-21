import type { MessageBlock } from "@redbamboo/chat"

export function useNovaEmotion(
  _messages: MessageBlock[],
  _isStreaming: boolean,
) {
  return { emotion: "idle" as const, src: "/api/avatar" }
}
