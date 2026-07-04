import { useRef, useEffect, useMemo, useCallback } from "react"
import { ScrollArea } from "@redbamboo/ui"
import { Composer, MarkdownRenderer } from "@redbamboo/chat"
import type { MessageBlock, ImageAttachment, PendingQuestion } from "@redbamboo/chat"
import type { EventType } from "../../lib/types"

type TimelineEntry =
  | { kind: "time-marker"; time: string; key: string }
  | { kind: "event"; icon: string; iconColor?: string; text: string; timestamp: Date; key: string }
  | { kind: "message"; role: "user" | "assistant"; content: string; timestamp: Date; key: string; isStreaming?: boolean }

function isEvent(msg: MessageBlock): boolean {
  const source = msg.metadata?.source as string | undefined
  if (source?.startsWith("event:")) return true
  const text = msg.parts.find((p) => p.type === "text")?.content ?? ""
  return /<nova-event\s/.test(text)
}

function extractEventContent(msg: MessageBlock): string {
  const text = msg.parts.find((p) => p.type === "text")?.content ?? ""
  const match = text.match(/<nova-event[^>]*>([\s\S]*?)<\/nova-event>/)
  return match?.[1]?.trim() ?? text
}

function extractEventSource(msg: MessageBlock): string {
  const source = msg.metadata?.source as string | undefined
  if (source) return source
  const text = msg.parts.find((p) => p.type === "text")?.content ?? ""
  const match = text.match(/<nova-event\s+source="([^"]*)"/)
  return match?.[1] ? `event:${match[1]}` : "event:unknown"
}

function formatTime(date: Date): string {
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false })
}

function buildEntries(
  messages: MessageBlock[],
  isStreaming: boolean,
  resolveEventType: (source: string) => EventType,
): TimelineEntry[] {
  const entries: TimelineEntry[] = []
  let lastMarkerSlot = ""

  for (let i = 0; i < messages.length; i++) {
    const msg = messages[i]!
    const ts = new Date(msg.timestamp ?? Date.now())
    const slot = `${ts.getHours()}:${String(Math.floor(ts.getMinutes() / 15) * 15).padStart(2, "0")}`

    if (slot !== lastMarkerSlot) {
      lastMarkerSlot = slot
      entries.push({ kind: "time-marker", time: formatTime(new Date(ts.getFullYear(), ts.getMonth(), ts.getDate(), ts.getHours(), Math.floor(ts.getMinutes() / 15) * 15)), key: `tm-${slot}` })
    }

    const streaming = isStreaming && i === messages.length - 1 && msg.role === "assistant"

    if (isEvent(msg)) {
      const source = extractEventSource(msg)
      const eventType = resolveEventType(source)
      const text = extractEventContent(msg)
      entries.push({
        kind: "event",
        icon: eventType.icon ?? "fa-solid fa-circle-dot",
        iconColor: eventType.color ?? undefined,
        text,
        timestamp: ts,
        key: msg.id,
      })
    } else {
      const text = msg.parts.find((p) => p.type === "text")?.content ?? ""
      entries.push({ kind: "message", role: msg.role, content: text, timestamp: ts, key: msg.id, isStreaming: streaming })
    }
  }

  return entries
}

interface LiveTimelineProps {
  messages: MessageBlock[]
  isStreaming: boolean
  onSend: (content: string, images?: ImageAttachment[]) => void
  onInterrupt: () => void
  resolveEventType: (source: string) => EventType
  assistantAvatar?: string
  resolveImageSrc?: (src: string) => string | undefined
  header?: React.ReactNode
  disabled?: boolean
  placeholder?: string
  sessionId?: string | null
  pendingQuestion?: PendingQuestion | null
  onAnswerQuestion?: (answer: string) => void
  onResume?: () => void | Promise<void>
}

export function LiveTimeline({ messages, isStreaming, onSend, onInterrupt, resolveEventType, assistantAvatar, resolveImageSrc, header, disabled, placeholder, sessionId, pendingQuestion, onAnswerQuestion, onResume }: LiveTimelineProps) {
  const bottomRef = useRef<HTMLDivElement>(null)
  const entries = useMemo(() => buildEntries(messages, isStreaming, resolveEventType), [messages, isStreaming, resolveEventType])

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" })
  }, [entries.length])

  const handleSend = useCallback((content: string, images?: ImageAttachment[]) => {
    onSend(content, images)
  }, [onSend])

  return (
    <div className="flex-1 flex flex-col min-h-0 min-w-0">
      {header}
      <ScrollArea className="flex-1">
        <div className="px-4 py-3 space-y-0.5">
          {entries.length === 0 && (
            <div className="flex items-center justify-center h-32 text-text-muted text-sm">
              <div className="text-center">
                <i className="fa-solid fa-tower-broadcast text-2xl opacity-30 mb-2" />
                <p>Timeline is empty</p>
              </div>
            </div>
          )}

          {entries.map((entry) => {
            if (entry.kind === "time-marker") {
              return (
                <div key={entry.key} className="flex items-center gap-3 py-2">
                  <div className="h-px flex-1 bg-overlay-6" />
                  <span className="text-[11px] text-text-muted font-mono tabular-nums">{entry.time}</span>
                  <div className="h-px flex-1 bg-overlay-6" />
                </div>
              )
            }

            if (entry.kind === "event") {
              return (
                <div key={entry.key} className="flex items-start gap-2.5 py-1 group/event">
                  <div className="w-5 h-5 flex items-center justify-center shrink-0 mt-0.5">
                    <i className={`${entry.icon} text-xs`} style={entry.iconColor ? { color: entry.iconColor } : { color: "var(--color-text-muted)" }} />
                  </div>
                  <span className="text-sm text-text-secondary flex-1 min-w-0 leading-relaxed">{entry.text}</span>
                  <span className="text-[10px] text-text-muted font-mono tabular-nums opacity-0 group-hover/event:opacity-100 transition-opacity shrink-0 mt-0.5">
                    {formatTime(entry.timestamp)}
                  </span>
                </div>
              )
            }

            if (entry.kind === "message") {
              const isAssistant = entry.role === "assistant"
              return (
                <div key={entry.key} className="py-2">
                  <div
                    className={[
                      "rounded-lg px-3 py-2 text-sm",
                      isAssistant
                        ? "border-l-2 border-accent-teal bg-overlay-4"
                        : "border-l-2 border-overlay-15 bg-overlay-4",
                    ].join(" ")}
                  >
                    <div className="flex items-center gap-2 mb-1">
                      {isAssistant && assistantAvatar && (
                        <img src={assistantAvatar} alt="" className="w-4 h-4 rounded-full object-cover" />
                      )}
                      <span className="text-[10px] text-text-muted font-mono tabular-nums">
                        {formatTime(entry.timestamp)}
                      </span>
                      {entry.isStreaming && (
                        <i className="fa-solid fa-circle text-[6px] text-accent-teal animate-pulse" />
                      )}
                    </div>
                    <div className="prose-sm">
                      <MarkdownRenderer content={entry.content} resolveImageSrc={resolveImageSrc} />
                    </div>
                  </div>
                </div>
              )
            }

            return null
          })}

          {/* Now indicator */}
          <div className="flex items-center gap-3 py-3">
            <div className="h-px flex-1 bg-accent-teal/30" />
            <div className="flex items-center gap-1.5">
              <span className="w-2 h-2 rounded-full bg-accent-teal animate-pulse" />
              <span className="text-[11px] text-accent-teal font-medium">Now</span>
            </div>
            <div className="h-px flex-1 bg-accent-teal/30" />
          </div>

          <div ref={bottomRef} />
        </div>
      </ScrollArea>

      <Composer
        onSend={handleSend}
        onInterrupt={onInterrupt}
        disabled={!!disabled}
        isStreaming={isStreaming}
        placeholder={placeholder}
        sessionId={sessionId}
        pendingQuestion={!!pendingQuestion}
        onAnswerQuestion={onAnswerQuestion}
        onResume={onResume}
        draftStorageKey="nova-drafts"
      />
    </div>
  )
}
