import { useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from "react"
import { ChatMessage, MarkdownRenderer, PART_COLOR, USER_BUBBLE_SHAPE_STYLE, type AgentNoteExchangeSlot } from "@redbamboo/chat"
import { Badge, Button, DialogHeader, DialogTitle, EntityCard } from "@redbamboo/ui"
import { api } from "../../lib/api"
import { createDiscussionTitleResolver } from "../../lib/discussion-display-title"
import { discussionLabel, groupingIcon, type DiscussionGroupingView } from "../../lib/discussion-grouping"
import { useDiscussionThumbnailSource } from "./discussion-thumbnail"
import type { DiscussionInfo } from "../../lib/types"

export interface ExchangeNote {
  uid: string
  direction: "sent" | "received"
  content: string
  timestamp?: string
  agentId: string | null
  failed: boolean
}

interface ExchangeResponse {
  discussion: DiscussionInfo
  notes: ExchangeNote[]
}

type ExchangeLoad =
  | { kind: "loading" }
  | { kind: "error" }
  | { kind: "ready"; exchange: ExchangeResponse }

interface NoteDiscussion {
  id: string
  title: string
  agentId: string | null
  summary?: DiscussionInfo
  canOpen: boolean
}

interface DiscussionVisualSettings {
  grouping: DiscussionGroupingView | null
  showAvatars: boolean
  useThumbnails: boolean
}

interface AgentNoteExchangeProps extends Omit<AgentNoteExchangeSlot, "part"> {
  visualSettings: DiscussionVisualSettings
  current: NoteDiscussion
  other: NoteDiscussion
  clicked: ExchangeNote
  onOpenDiscussion: (id: string) => void
  resolveAgent: (id: string) => { name: string; avatarUrl?: string | null } | undefined
  resolveImageSrc: (src: string) => string | undefined
  resolveFileLink: (path: string, opts?: { line?: number }) => (() => void) | undefined
}

const noteDay = new Intl.DateTimeFormat([], { dateStyle: "medium" })
const noteTime = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" })

function timeOf(note: ExchangeNote) {
  const value = note.timestamp ? Date.parse(note.timestamp) : NaN
  return Number.isNaN(value) ? undefined : value
}

function withOpenedExchangeNote(notes: ExchangeNote[], clicked: ExchangeNote): ExchangeNote[] {
  if (notes.some(note => note.uid === clicked.uid)) return notes
  const timestamp = timeOf(clicked)
  const index = timestamp === undefined ? -1 : notes.findIndex(note => (timeOf(note) ?? Infinity) > timestamp)
  return index === -1 ? [...notes, clicked] : [...notes.slice(0, index), clicked, ...notes.slice(index)]
}

function ExchangeDiscussionCard({ discussion, current, agent, onOpen, visualSettings }: {
  discussion: NoteDiscussion
  current?: boolean
  agent?: ReturnType<AgentNoteExchangeProps["resolveAgent"]>
  onOpen: (id: string) => void
  visualSettings: DiscussionVisualSettings
}) {
  const { summary } = discussion
  const showVisual = visualSettings.showAvatars && !!summary
  const label = showVisual && summary ? discussionLabel(summary, visualSettings.grouping) : undefined
  const target = visualSettings.useThumbnails ? label?.thumbnailTarget : undefined
  const { ref, source, onError } = useDiscussionThumbnailSource<HTMLDivElement>(discussion.id, target)
  const onClick = (event: MouseEvent<HTMLAnchorElement>) => {
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    onOpen(discussion.id)
  }
  const icon = label ? "ph ph-" + groupingIcon(label.icon)
    : summary?.type === "live" ? "ph ph-pulse"
      : summary?.type === "heartbeat" ? "ph ph-heartbeat"
        : summary?.status === "archived" ? "ph ph-archive" : "ph ph-chat-circle-dots"
  return (
    <div ref={ref} className="min-w-0 self-stretch" onErrorCapture={source ? onError : undefined}>
      <EntityCard
        className={"h-full min-w-0 [&_[data-slot=entity-name]]:whitespace-normal [&_[data-slot=entity-name]]:break-words [&_[data-slot=entity-subtitle]]:whitespace-normal"
          + (label ? " [&_[data-slot=entity-media]]:grid [&_[data-slot=entity-media]]:size-8 [&_[data-slot=entity-media]]:place-items-center [&_[data-slot=entity-media]]:rounded [&_[data-slot=entity-media]]:bg-overlay-6" : "")}
        entity={{ id: discussion.id, typeSlug: "discussion", name: discussion.title }}
        visual={{ icon, src: label ? source : showVisual ? agent?.avatarUrl : undefined }}
        variant="outlined"
        subtitle={current ? ["This discussion", agent?.name].filter(Boolean).join(" · ") : agent?.name}
        current={current}
        action={discussion.canOpen ? {
          kind: "link", href: "/apps/nova/chat/" + encodeURIComponent(discussion.id),
          ariaLabel: "Open " + discussion.title, onClick,
        } : undefined}
      />
    </div>
  )
}

function NoteMessage({ note, selected, agent, links }: {
  note: ExchangeNote
  selected: boolean
  agent?: { name: string; avatarUrl?: string | null }
  links: Pick<AgentNoteExchangeProps, "resolveImageSrc" | "resolveFileLink">
}) {
  const received = note.direction === "received"
  const timestamp = timeOf(note)
  return (
    <div
      className={received ? "ml-auto w-fit max-w-[90%] min-w-0 sm:max-w-[82%]" : "mr-auto w-fit max-w-full min-w-0 sm:max-w-[90%]"}
      aria-current={selected || undefined}
      data-slot="agent-note-exchange-note"
      data-note-uid={note.uid}
      data-direction={note.direction}
    >
      <div className={"mb-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-text-muted" + (received ? " justify-end" : "")}>
        {agent?.avatarUrl && <img className="size-5 shrink-0 rounded-full object-cover" src={agent.avatarUrl} alt="" />}
        {agent?.name && <span className="font-medium text-contrast">{agent.name}</span>}
        <span>{received ? "Received" : "Sent"}</span>
        {timestamp !== undefined && <time className="font-mono text-[10px]" dateTime={note.timestamp}>{noteTime.format(timestamp)}</time>}
        {note.failed && <span className="text-destructive">Not delivered</span>}
        {selected && (
          <Badge variant="secondary" className="text-contrast"
            style={{ backgroundColor: "color-mix(in srgb, " + PART_COLOR.delegation + " 18%, transparent)" }}>
            <span aria-hidden className="size-1.5 rounded-full" style={{ backgroundColor: PART_COLOR.delegation }} />
            Opened note
          </Badge>
        )}
      </div>
      {received ? (
        <div data-chat-user-bubble className="relative bg-overlay-10 px-4 py-2.5" style={USER_BUBBLE_SHAPE_STYLE}>
          <div data-chat-user-text className="text-sm font-serif markdown-body [&>:first-child]:mt-0 [&>:last-child]:mb-0">
            <MarkdownRenderer content={note.content} {...links} />
          </div>
        </div>
      ) : (
        <ChatMessage
          block={{ id: note.uid, role: "assistant", timestamp: note.timestamp ?? "", parts: [{ type: "text", content: note.content }] }}
          animateEntrance={false}
          compactAfter
          showActions={false}
          {...links}
        />
      )}
    </div>
  )
}

export function AgentNoteExchange({
  open, current, other, clicked, navigation, queueStatus, onClose, onOpenDiscussion, resolveAgent, resolveImageSrc, resolveFileLink, visualSettings,
}: AgentNoteExchangeProps) {
  const [load, setLoad] = useState<ExchangeLoad>({ kind: "loading" })
  const [attempt, setAttempt] = useState(0)
  const list = useRef<HTMLOListElement>(null)
  const scrollArea = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    setLoad({ kind: "loading" })
    const query = new URLSearchParams({ other: other.id })
    api.get<ExchangeResponse>(`/api/apps/nova/discussions/${encodeURIComponent(current.id)}/note-exchange?${query}`, controller.signal)
      .then(exchange => {
        if (!controller.signal.aborted) setLoad({ kind: "ready", exchange })
      })
      .catch(() => {
        if (!controller.signal.aborted) setLoad({ kind: "error" })
      })
    return () => controller.abort()
  }, [attempt, current.id, open, other.id])

  useLayoutEffect(() => {
    if (!open) return
    const selected = list.current?.querySelector<HTMLElement>('[aria-current="true"]')
    const viewport = scrollArea.current
    if (selected && viewport) {
      viewport.scrollTop += selected.getBoundingClientRect().top - viewport.getBoundingClientRect().top - 52
    }
  }, [open, clicked.uid, load])

  const exchange = load.kind === "ready" ? load.exchange : undefined
  const notes = withOpenedExchangeNote(exchange?.notes ?? [], clicked)
  const counterpart = exchange?.discussion
  const displayOther: NoteDiscussion = counterpart ? {
    id: counterpart.id,
    title: createDiscussionTitleResolver([counterpart])(counterpart) ?? other.title,
    agentId: counterpart.agentId,
    summary: counterpart,
    canOpen: counterpart.status !== "archived" && counterpart.status !== "archiving",
  } : other
  const openDiscussion = (id: string) => {
    onClose()
    if (id !== current.id) onOpenDiscussion(id)
  }
  const discussionAgent = (room: NoteDiscussion) => room.agentId ? resolveAgent(room.agentId) : undefined
  const links = { resolveImageSrc, resolveFileLink }

  return (
    <>
      <DialogHeader className="shrink-0 gap-3 border-b border-border-subtle px-4 pt-3 pb-3 sm:px-5 sm:pt-4" data-slot="agent-note-dialog-header">
        <div className="flex min-w-0 items-center justify-between gap-2 pr-8">
          <DialogTitle className="m-0">Discussion exchange</DialogTitle>
          {navigation}
        </div>
        <div className="grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-2 sm:gap-3">
          <ExchangeDiscussionCard discussion={current} current agent={discussionAgent(current)} onOpen={openDiscussion} visualSettings={visualSettings} />
          <i aria-hidden className="ph ph-arrows-left-right text-text-muted" />
          <ExchangeDiscussionCard discussion={displayOther} agent={discussionAgent(displayOther)} onOpen={openDiscussion} visualSettings={visualSettings} />
        </div>
      </DialogHeader>
      {queueStatus}
      <div ref={scrollArea} role="region" aria-label="Exchange notes" tabIndex={0}
        className="min-h-0 flex-1 overflow-y-auto overscroll-contain focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring">
        <div className="px-4 py-4 sm:px-5" data-slot="agent-note-body">
          {load.kind === "loading" && <p role="status" className="mb-3 text-xs text-text-muted">Loading exchange…</p>}
          {load.kind === "error" && (
            <div className="mb-3 flex items-center gap-2 text-xs text-text-muted">
              <span role="status">Exchange history unavailable.</span>
              <Button variant="ghost" size="xs" onClick={() => setAttempt(value => value + 1)}>Retry</Button>
            </div>
          )}
          <ol ref={list} className="flex min-w-0 flex-col gap-4" aria-label="Notes in chronological order">
            {notes.map((note, index) => {
              const timestamp = timeOf(note)
              const day = timestamp === undefined ? null : noteDay.format(timestamp)
              const previous = index > 0 ? timeOf(notes[index - 1]) : undefined
              const agentId = note.agentId ?? (note.direction === "sent" ? current.agentId : displayOther.agentId)
              return (
                <li key={note.uid} className="min-w-0">
                  {day && (previous === undefined || noteDay.format(previous) !== day) && (
                    <div className="mb-4 flex items-center gap-3 text-xs text-text-muted">
                      <span aria-hidden className="h-px flex-1 bg-overlay-10" />
                      <time dateTime={note.timestamp}>{day}</time>
                      <span aria-hidden className="h-px flex-1 bg-overlay-10" />
                    </div>
                  )}
                  <NoteMessage note={note} selected={note.uid === clicked.uid}
                    agent={agentId ? resolveAgent(agentId) : undefined} links={links} />
                </li>
              )
            })}
          </ol>
        </div>
      </div>
    </>
  )
}
