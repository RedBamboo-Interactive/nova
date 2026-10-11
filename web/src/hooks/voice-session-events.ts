export interface VoiceCaptionState {
  speaker: "user" | "assistant" | null
  user: string
  assistant: string
}

export function applyVoiceEvent(captions: VoiceCaptionState, event: Record<string, unknown>): VoiceCaptionState {
  const type = typeof event.type === "string" ? event.type : ""
  const delta = typeof event.delta === "string" ? event.delta : ""
  if (!delta) return captions
  if (type === "session.input_transcript.delta") {
    return {
      speaker: "user",
      user: captions.speaker === "user" ? captions.user + delta : delta,
      assistant: captions.speaker === "user" ? captions.assistant : "",
    }
  }
  if (type === "session.output_transcript.delta") {
    return {
      speaker: "assistant",
      user: captions.speaker === "assistant" ? captions.user : "",
      assistant: captions.speaker === "assistant" ? captions.assistant + delta : delta,
    }
  }
  return captions
}
