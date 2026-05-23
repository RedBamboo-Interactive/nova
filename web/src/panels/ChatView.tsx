import { ChatPanel } from "@redbamboo/chat"
import type { ChatBackend } from "@redbamboo/chat"

interface ChatViewProps {
  backend: ChatBackend
}

export function ChatView({ backend }: ChatViewProps) {
  return (
    <div className="h-full flex flex-col">
      <ChatPanel backend={backend} placeholder="Talk to Nova..." />
    </div>
  )
}
