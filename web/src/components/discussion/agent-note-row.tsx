import { memo, useState, type MouseEvent } from "react"
import { MarkdownRenderer, parseEventPart, resolveChatMediaSrc } from "@redbamboo/chat"
import type { EventQueueStatus, MessagePart } from "@redbamboo/chat"
import { describeNote, describeSentNote, isSentNotePart, notePreview, type NoteOriginDiscussion } from "../../lib/agent-note-row"

interface AgentNoteRowProps {
  part: MessagePart
  queueStatus?: EventQueueStatus
  /** Still in the queue: the dashed edge and fade a waiting message has. The status line sits below, outside the row. */
  waiting?: boolean
  currentDiscussionId: string | null
  resolveAgent: (ref: string) => { name: string; avatarUrl?: string | null } | undefined
  resolveDiscussion: (id: string) => NoteOriginDiscussion | undefined
  onOpenDiscussion: (id: string) => void
  resolveImageSrc?: (src: string) => string | undefined
  resolveFileLink?: (filePath: string, opts?: { line?: number }) => (() => void) | undefined
}

const stringOrNull = (value: unknown) => typeof value === "string" && value ? value : null

// Tappable, without an underline as loud as the words it sits under.
const ORIGIN_UNDERLINE = { textDecorationColor: "color-mix(in oklch, currentColor 25%, transparent)" }

// Inline for the reason USER_BUBBLE_SHAPE_STYLE is: a later plugin stylesheet can reset a rounded-*.
// All four corners round, because the tail is what makes a bubble the person's own.
const NOTE_SHAPE = { borderRadius: "calc(var(--radius) * 1.4)" }

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
 * What one Agent instance wrote to another. A note received sits on the right because
 * that is where things said to this Agent go; a note sent sits on the left, where this
 * Agent speaks. Every step of it is quieter than the person's own bubble: fainter fill,
 * muted smaller text, no tail, and who it's from or to on top.
 */
export const AgentNoteRow = memo(function AgentNoteRow({
  part,
  queueStatus,
  waiting,
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

  const sent = isSentNotePart(part)
  const sentTo = sent ? describeSentNote({
    targetDiscussionId: stringOrNull(event.data?.targetDiscussionId),
    targetTitle: stringOrNull(event.data?.targetTitle),
    targetAgentId: stringOrNull(event.data?.targetAgentId),
    resolveAgent,
    resolveDiscussion,
  }) : null
  const from = sentTo ? null : describeNote({
    agent: stringOrNull(event.data?.agent),
    discussionId: stringOrNull(event.data?.discussionId),
    topic: stringOrNull(event.data?.topic),
    currentDiscussionId,
    resolveAgent,
    resolveDiscussion,
  })
  const name = sentTo ? sentTo.name ?? "another discussion" : from!.name
  const avatarUrl = sentTo ? sentTo.avatarUrl : from!.avatarUrl
  const origin = sentTo ? sentTo.target : from!.origin
  const openDiscussionId = sentTo ? sentTo.openDiscussionId : from!.openDiscussionId
  const lead = sentTo ? (sentTo.name ? `To ${sentTo.name} in` : "To") : `${name} in`
  const failed = sent && event.data?.failed === true
  const avatar = avatarUrl ? resolveChatMediaSrc(avatarUrl, resolveImageSrc) : undefined
  const time = expanded ? formatNoteTime(event.timestamp) : null
  const posted = expanded ? formatNoteTime(event.postedAt) : null
  const postedAt = posted === time ? null : posted
  const toggle = () => setExpanded(value => !value)

  return (
    <div
      data-slot="agent-note-row"
      data-direction={sent ? "sent" : "received"}
      data-waiting={waiting || undefined}
      data-expanded={expanded || undefined}
      className={`${sent ? "" : "ml-auto "}w-fit min-w-0 cursor-pointer bg-overlay-4 px-3.5 py-2 ${expanded ? "max-w-[92%]" : "max-w-[80%]"} ${waiting ? "border border-dashed border-overlay-20 opacity-60 transition-opacity hover:opacity-90" : ""}`}
      style={NOTE_SHAPE}
      onClick={toggle}
    >
      <div className="min-w-0">
        <div className="flex min-w-0 items-center gap-1 text-[11px] leading-[18px] text-text-muted">
          {avatar ? (
            <img src={avatar} alt="" className="mr-0.5 size-3.5 shrink-0 rounded-full object-cover" />
          ) : (
            <span aria-hidden className="mr-0.5 grid size-3.5 shrink-0 place-items-center rounded-full bg-overlay-8 text-[8px] font-medium">
              {name.charAt(0).toUpperCase()}
            </span>
          )}
          <span className="shrink-0">{lead}</span>
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
          <span className="ml-auto flex shrink-0 items-center gap-2 pl-2">
            {postedAt && <span className="font-mono text-[10px] tabular-nums text-text-disabled">Posted {postedAt}</span>}
            {time && <span className="font-mono text-[10px] tabular-nums text-text-disabled">{time}</span>}
            <button
              type="button"
              aria-expanded={expanded}
              aria-label={`${expanded ? "Collapse" : "Read"} the note ${sent ? "to" : "from"} ${name}`}
              data-slot="agent-note-toggle"
              onClick={click => { click.stopPropagation(); toggle() }}
              className="cursor-pointer text-text-disabled transition-colors hover:text-text-muted"
            >
              <i className={`ph-bold ${expanded ? "ph-caret-up" : "ph-caret-down"} text-[9px]`} />
            </button>
          </span>
        </div>
        {!expanded && (
          <p data-slot="agent-note-preview" className="line-clamp-2 font-serif text-[13px] leading-5 text-text-muted">
            {notePreview(event.text)}
          </p>
        )}
        {expanded && (
          <div
            data-slot="agent-note-body"
            className="mt-0.5 text-[13.5px] leading-[21px] font-serif text-text-muted markdown-body break-words [&>:first-child]:mt-0 [&>:last-child]:mb-0"
            onClick={keepInteractionInBody}
          >
            <MarkdownRenderer content={event.text} resolveImageSrc={resolveImageSrc} resolveFileLink={resolveFileLink} />
          </div>
        )}
        {failed && (
          <div data-slot="agent-note-failed" className="text-[11px] leading-5 text-red-400">Not delivered</div>
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
