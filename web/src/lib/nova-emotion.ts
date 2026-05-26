import type { MessageBlock, MessagePart } from "@redbamboo/chat"

export type NovaEmotion = string

export interface EmotionContext {
  isStreaming: boolean
  parts: MessagePart[]
  textContent: string
}

export interface EmotionRule {
  emotion: NovaEmotion
  priority?: number
  duration?: number
  match: (ctx: EmotionContext) => boolean
}

const EXCITEMENT_PATTERN = /!|\bthat's|hell yeah|nice|love|perfect|awesome|nailed|clean|spot on/i
const FRUSTRATION_PATTERN = /\berror\b|failed|broken|can't|crash|bug|damn|ugh/i
const GREETING_PATTERN = /^(hey|hi|hello|yo|morning|evening|what's up|howdy|bonjour|salut|coucou)\b/i

export const defaultRules: EmotionRule[] = [
  {
    emotion: "thinking",
    match: (ctx) =>
      ctx.isStreaming &&
      ctx.parts.length > 0 &&
      !ctx.parts.some(p => p.type === "text" && p.content.length > 0),
  },
  {
    emotion: "focused",
    match: (ctx) =>
      ctx.isStreaming &&
      ctx.parts.some(p => p.type === "tool_use"),
  },
  {
    emotion: "frustrated",
    duration: 5000,
    match: (ctx) =>
      ctx.parts.some(p => p.type === "error") ||
      (!ctx.isStreaming && FRUSTRATION_PATTERN.test(ctx.textContent.slice(-200))),
  },
  {
    emotion: "greeting",
    duration: 4000,
    match: (ctx) =>
      !ctx.isStreaming && GREETING_PATTERN.test(ctx.textContent.trim()),
  },
  {
    emotion: "happy",
    duration: 4000,
    match: (ctx) =>
      !ctx.isStreaming && EXCITEMENT_PATTERN.test(ctx.textContent.slice(-300)),
  },
]

export function resolveEmotion(
  rules: EmotionRule[],
  ctx: EmotionContext,
): NovaEmotion {
  for (const rule of rules) {
    if (rule.match(ctx)) return rule.emotion
  }
  return "idle"
}

export function emotionDuration(rules: EmotionRule[], emotion: NovaEmotion): number | undefined {
  return rules.find(r => r.emotion === emotion)?.duration
}

export function buildEmotionContext(
  messages: MessageBlock[],
  isStreaming: boolean,
): EmotionContext {
  const lastAssistant = messages.findLast(m => m.role === "assistant")

  const parts = lastAssistant?.parts ?? []
  const textContent = parts
    .filter(p => p.type === "text")
    .map(p => p.content)
    .join("\n")

  return { isStreaming, parts, textContent }
}
