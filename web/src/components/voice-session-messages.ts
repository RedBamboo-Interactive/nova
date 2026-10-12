import type { MessageBlock } from "@redbamboo/chat"

export type VoiceCaptionSpeaker = "user" | "assistant" | null

interface VoiceCaptionProjection {
  speaker: VoiceCaptionSpeaker
  userCaption: string
  assistantCaption: string
  timestamp: string
}

function normalizeCaption(value: string) {
  return value.trim().replace(/\s+/g, " ")
}

function canonicalTailContains(messages: MessageBlock[], role: "user" | "assistant", caption: string) {
  const normalizedCaption = normalizeCaption(caption)
  if (!normalizedCaption) return true
  let checked = 0
  for (let index = messages.length - 1; index >= 0 && checked < 3; index--) {
    const block = messages[index]
    if (block.role !== role) continue
    checked++
    const textParts = block.parts.filter(part => part.type === "text").map(part => normalizeCaption(part.content))
    if (textParts.some(text => text === normalizedCaption) || normalizeCaption(textParts.join(" ")) === normalizedCaption) return true
  }
  return false
}

/**
 * Add the currently audible Live caption to a fresh presentation array.
 * These reserved-ID blocks never enter the canonical transcript, queue or
 * reconciliation path; they exist only for the controlled voice-mode view.
 */
export function projectVoiceCaption(messages: MessageBlock[], projection: VoiceCaptionProjection): MessageBlock[] {
  const role = projection.speaker
  if (!role) return messages
  const caption = role === "user" ? projection.userCaption : projection.assistantCaption
  if (!normalizeCaption(caption) || canonicalTailContains(messages, role, caption)) return messages
  return [
    ...messages,
    {
      id: `voice-caption-${role}`,
      role,
      timestamp: projection.timestamp,
      metadata: { transientVoiceCaption: true },
      parts: [{
        type: "text",
        content: caption,
        isPartial: role === "assistant",
      }],
    },
  ]
}
