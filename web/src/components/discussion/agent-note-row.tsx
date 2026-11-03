import { memo, useState, type MouseEvent } from "react"
import { MarkdownRenderer, parseEventPart, resolveChatMediaSrc } from "@redbamboo/chat"
import type { EventQueueStatus, MessagePart } from "@redbamboo/chat"
import { describeNote, notePreview, type NoteOriginDiscussion } from "../../lib/agent-note-row"

interface AgentNoteRowProps {
  part: MessagePart
  queueStatus?: EventQueueStatus
  currentDiscussionId: string | null
  resolveAgent: (ref: string) => { name: string; avatarUrl?: string | null } | undefined
  resolveDiscussion: (id: string) => NoteOriginDiscussion | undefined
  onOpenDiscussion: (id: string) => void
  resolveImageSrc?: (src: string) => string | undefined
  resolveFileLink?: (filePath: string, opts?: { line?: number }) => (() => void) | undefined
}

const stringOrNull = (value: unknown) => typeof value === "string" && value ? value : null

// Tappable, without an underline as loud as the words it sits under.
const ORIGIN_UNDERLINE = { textDecorationColor: "color-mix(in oklch, currentColor 35%, transparent)" }

/** A tap on the prose collapses a long note, but a link, an image or a selection stays what it is. */
function keepInteractionInBody(click: MouseEvent<HTMLDivElement>) {
  const target = click.target as Element
  const selecting = !!target.ownerDocument.defaultView?.getSelection()?.toString()
  if (selecting || target.closest("a, button, img, video, audio")) click.stopPropagation()
}

function formatNoteTime(iso: string | undefined): string | null {
  if (!iso) return null
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return null
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false })
}

/**
 * What one Agent instance wrote to another, as a quiet line in the conversation:
 * who, from where, and the first line of it. It reads as something overheard
 * rather than something said, so nothing here is louder than the conversation.
 */
export const AgentNoteRow = memo(function AgentNoteRow({
  part,
  queueStatus,
  currentDiscussionId,
  resolveAgent,
  resolveDiscussion,
  onOpenDiscussion,
  resolveImageSrc,
  resolveFileLink,
}: AgentNoteRowProps) {
  const [expanded, setExpanded] = useState(false)
  const event = parseEventPart(part)
  if (!event) return null

  const { name, avatarUrl, origin, openDiscussionId } = describeNote({
    agent: stringOrNull(event.data?.agent),
    discussionId: stringOrNull(event.data?.discussionId),
    topic: stringOrNull(event.data?.topic),
    currentDiscussionId,
    resolveAgent,
    resolveDiscussion,
  })
  const avatar = avatarUrl ? resolveChatMediaSrc(avatarUrl, resolveImageSrc) : undefined
  const time = expanded ? formatNoteTime(event.timestamp) : null
  const toggle = () => setExpanded(value => !value)

  return (
    <div
      data-slot="agent-note-row"
      data-expanded={expanded || undefined}
      className="flex min-w-0 cursor-pointer items-start gap-2"
      onClick={toggle}
    >
      {avatar ? (
        <img src={avatar} alt="" className="mt-0.5 size-5 shrink-0 rounded-full object-cover" />
      ) : (
        <span aria-hidden className="mt-0.5 grid size-5 shrink-0 place-items-center rounded-full bg-overlay-8 text-[10px] font-medium text-text-muted">
          {name.charAt(0).toUpperCase()}
        </span>
      )}
      <div className="min-w-0 flex-1">
        <div className="flex min-w-0 items-baseline gap-1 text-xs leading-5 text-text-muted">
          <span className="shrink-0 font-medium">{name},</span>
          <span className="shrink-0">from</span>
          {openDiscussionId ? (
            <button
              type="button"
              data-slot="agent-note-origin"
              onClick={click => { click.stopPropagation(); onOpenDiscussion(openDiscussionId) }}
              className="min-w-0 cursor-pointer truncate underline underline-offset-2 transition-colors hover:text-text-secondary"
              style={ORIGIN_UNDERLINE}
              title="Open this discussion"
            >
              {origin}
            </button>
          ) : (
            <span data-slot="agent-note-origin" className="min-w-0 truncate">{origin}</span>
          )}
          <span className="ml-auto flex shrink-0 items-baseline gap-2 pl-2">
            {time && <span className="font-mono text-[10px] tabular-nums text-text-disabled">{time}</span>}
            <button
              type="button"
              aria-expanded={expanded}
              aria-label={expanded ? `Collapse the note from ${name}` : `Read the note from ${name}`}
              data-slot="agent-note-toggle"
              onClick={click => { click.stopPropagation(); toggle() }}
              className="cursor-pointer text-text-disabled transition-colors hover:text-text-muted"
            >
              <i className={`ph-bold ${expanded ? "ph-caret-up" : "ph-caret-down"} text-[9px]`} />
            </button>
          </span>
        </div>
        {!expanded && (
          <p data-slot="agent-note-preview" className="truncate text-xs leading-5 text-text-disabled">
            {notePreview(event.text)}
          </p>
        )}
        {expanded && (
          <div
            data-slot="agent-note-body"
            className="mt-0.5 pb-1 text-sm leading-relaxed font-serif text-text-secondary markdown-body break-words"
            onClick={keepInteractionInBody}
          >
            <MarkdownRenderer content={event.text} resolveImageSrc={resolveImageSrc} resolveFileLink={resolveFileLink} />
          </div>
        )}
        {queueStatus && (
          <div
            data-slot="agent-note-queue"
            className="flex items-center gap-1 text-[11px] leading-5"
            onClick={click => click.stopPropagation()}
          >
            <span className={queueStatus.failed ? "text-red-400" : "text-text-disabled"}>{queueStatus.label}</span>
            {queueStatus.actions}
          </div>
        )}
      </div>
    </div>
  )
})
