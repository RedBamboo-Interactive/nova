import React, { useRef, useState } from "react"
import { createRoot } from "react-dom/client"
import type { MessageBlock } from "@redbamboo/chat"
import { VoiceSessionPanel } from "../../src/components/voice-session-panel"
import "./shell-preflight.css"
import "../../src/plugin.css"

const messages: MessageBlock[] = [
  {
    id: "earlier-user-question",
    role: "user",
    timestamp: "2026-10-12T17:40:54Z",
    parts: [{ type: "text", content: "Is this still the same discussion while we’re speaking?" }],
  },
  {
    id: "earlier-assistant-turn",
    role: "assistant",
    timestamp: "2026-10-12T17:40:55Z",
    parts: [{
      type: "text",
      phase: "final_answer",
      content: "It is. Voice is another way into this discussion, so our written context stays visible while we talk.",
    }],
  },
  {
    id: "user-question",
    role: "user",
    timestamp: "2026-10-12T17:41:00Z",
    parts: [{ type: "text", content: "Can you check what the real voice session is doing with my memory?" }],
  },
  {
    id: "assistant-turn",
    role: "assistant",
    timestamp: "2026-10-12T17:41:01Z",
    parts: [
      {
        type: "text",
        phase: "commentary",
        content: "I’m checking the canonical Nova session now. You’ll see the work here while we keep talking.",
      },
      {
        type: "tool_use",
        toolName: "Read",
        toolInput: JSON.stringify({ path: "memory/index.md" }),
        content: "",
      },
      {
        type: "tool_result",
        toolName: "Read",
        content: "Loaded the relevant memory index and voice-session decisions.",
      },
      {
        type: "text",
        phase: "final_answer",
        content: "Yes. This live voice is attached to the same ongoing discussion, so I can use our context and hand real tool work to the canonical Nova session.",
      },
    ],
  },
]

function Fixture() {
  const returnFocusRef = useRef<HTMLButtonElement>(null)
  const [muted, setMuted] = useState(false)
  const [endCount, setEndCount] = useState(0)
  return (
    <>
      <button ref={returnFocusRef} className="sr-only">Voice trigger</button>
      <output data-testid="end-count" className="sr-only">{endCount}</output>
      <VoiceSessionPanel
        agentName="Nova"
        avatarUrl="http://127.0.0.1:18804/api/assets/c0978012-eb6b-46a7-824e-170f2435ad79.png"
        phase="connected"
        muted={muted}
        delegating={false}
        startedAt={Date.now() - 83_000}
        error={null}
        userCaption="Can you check what the real voice session is doing with my memory?"
        assistantCaption="Yes. I’m working through the real Nova session now."
        captionSpeaker="assistant"
        messages={messages}
        isStreaming={false}
        returnFocusRef={returnFocusRef}
        onMute={() => setMuted(value => !value)}
        onEnd={() => setEndCount(value => value + 1)}
      />
    </>
  )
}

createRoot(document.getElementById("root")!).render(<Fixture />)
