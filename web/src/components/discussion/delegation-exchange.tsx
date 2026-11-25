import { useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from "react"
import { MarkdownRenderer, MESSAGE_PARAM, PART_COLOR, USER_BUBBLE_SHAPE_STYLE, type EventDetailsSlot } from "@redbamboo/chat"
import { Badge, Button, DialogHeader, DialogTitle, EntityCard } from "@redbamboo/ui"
import { api, ApiError } from "../../lib/api"
import { delegationSessionPath } from "../../lib/delegation-session-link"

type ExchangeEntryKind = "brief" | "follow-up" | "update" | "reply"

export interface DelegationEntry {
  uid: string
  kind: ExchangeEntryKind
  content: string
  timestamp?: string
  messageUid?: string | null
}

interface Exchange {
  title?: string | null
  repository?: string | null
  entries: DelegationEntry[]
}

interface DelegationExchangeProps extends Omit<EventDetailsSlot, "part"> {
  discussion: { id: string; title: string; agent?: string }
  sessionId: string
  opened: DelegationEntry
  revision: string | null
  navigate: (href: string) => void
}

const label: Record<ExchangeEntryKind, string> = {
  brief: "Delegated", "follow-up": "Follow-up", update: "Update", reply: "Replied",
}
const action: Record<ExchangeEntryKind, string> = {
  brief: "Open brief in Code", "follow-up": "Open follow-up in Code",
  update: "Open in Code", reply: "Open reply in Code",
}
const dayFormat = new Intl.DateTimeFormat([], { dateStyle: "medium" })
const timeFormat = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit", hourCycle: "h23" })

function readDate(timestamp: string | undefined) {
  if (!timestamp) return
  const parsed = Date.parse(timestamp)
  return Number.isNaN(parsed) ? undefined : parsed
}

function openLocally(event: MouseEvent<HTMLAnchorElement>, run: () => void) {
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
  event.preventDefault()
  run()
}

function Entry({ entry, selected, author, codePath, onOpen }: {
  entry: DelegationEntry
  selected: boolean
  author?: string
  codePath?: string
  onOpen: (href: string) => void
}) {
  const received = entry.kind === "update" || entry.kind === "reply"
  const reply = entry.kind === "reply"
  const date = readDate(entry.timestamp)
  const href = codePath && entry.messageUid ? codePath + "?" + new URLSearchParams({ [MESSAGE_PARAM]: entry.messageUid }) : undefined
  const [expanded, setExpanded] = useState(false)
  const restoreEntry = useRef(false)
  const entryRef = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    if (!restoreEntry.current) return
    restoreEntry.current = false
    entryRef.current?.scrollIntoView({ block: "nearest" })
  }, [expanded])
  const long = entry.content.length > 850
  const excerpt = long && !expanded
    ? entry.content.slice(0, 850).replace(/^#{1,6}\s+/gm, "").replace(/[\x60*]+/g, "")
    : null
  return (
    <div ref={entryRef} className={received ? "ml-auto w-fit max-w-[94%] min-w-0 sm:max-w-[82%]" : "mr-auto w-fit max-w-full min-w-0 sm:max-w-[90%]"}
      aria-current={selected || undefined} data-slot="delegation-exchange-entry" data-event-uid={entry.uid} data-kind={entry.kind}>
      <div className={"mb-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-text-muted" + (received ? " justify-end" : "")}>
        {author && <span className="font-medium text-contrast">{author}</span>}
        <span>{label[entry.kind]}</span>
        {date !== undefined && <time className="font-mono text-[10px]" dateTime={entry.timestamp}>{timeFormat.format(date)}</time>}
        {selected && (
          <Badge variant="secondary" className="text-contrast"
            style={{ backgroundColor: "color-mix(in srgb, " + PART_COLOR.delegation + " 18%, transparent)" }}>
            <span aria-hidden className="size-1.5 rounded-full" style={{ backgroundColor: PART_COLOR.delegation }} />
            Opened activity
          </Badge>
        )}
      </div>
      <div data-chat-user-bubble={reply ? "" : undefined} style={reply ? USER_BUBBLE_SHAPE_STYLE : undefined}
        className={reply ? "relative bg-overlay-10 px-4 py-2.5" : undefined}>
        {excerpt !== null ? (
          <p className="line-clamp-4 break-words font-serif text-sm text-contrast">{excerpt}</p>
        ) : (
          <div className={"break-words font-serif text-sm markdown-body [&>:first-child]:mt-0 [&>:last-child]:mb-0"
            + (entry.kind === "update" ? " text-text-muted" : " text-contrast")}>
            <MarkdownRenderer content={entry.content} />
          </div>
        )}
      </div>
      {(long || href) && (
        <div className={"mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-text-muted" + (received ? " justify-end" : "")}>
          {long && <button type="button" aria-expanded={expanded} onClick={() => {
            restoreEntry.current = expanded
            setExpanded(!expanded)
          }} className="font-medium underline underline-offset-2 hover:text-contrast">{expanded ? "Show less" : "Show full message"}</button>}
          {href && <a href={href} onClick={event => openLocally(event, () => onOpen(href))}
            className="underline underline-offset-2 hover:text-contrast">{action[entry.kind]}</a>}
        </div>
      )}
    </div>
  )
}

const cardClass = "h-full min-w-0 [&_[data-slot=entity-name]]:whitespace-normal [&_[data-slot=entity-name]]:break-words [&_[data-slot=entity-subtitle]]:whitespace-normal"

export function DelegationExchange({
  open, discussion, sessionId, opened, revision, navigation, queueStatus, onClose, navigate,
}: DelegationExchangeProps) {
  const [exchange, setExchange] = useState<Exchange>()
  const [load, setLoad] = useState<"loading" | "ready" | "unavailable" | "error">("loading")
  const [attempt, setAttempt] = useState(0)
  const scroll = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    setLoad("loading")
    const query = new URLSearchParams({ session: sessionId })
    api.get<Exchange>("/api/apps/nova/discussions/" + encodeURIComponent(discussion.id) + "/delegation-exchange?" + query, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return
        setExchange(value)
        setLoad("ready")
      })
      .catch(error => {
        if (controller.signal.aborted) return
        setExchange(undefined)
        setLoad(error instanceof ApiError && [401, 403, 404].includes(error.status) ? "unavailable" : "error")
      })
    return () => controller.abort()
  }, [attempt, discussion.id, open, revision, sessionId])

  useLayoutEffect(() => {
    if (!open) return
    const viewport = scroll.current
    const selected = viewport?.querySelector<HTMLElement>('[aria-current="true"]')
    if (!viewport || !selected) return
    const top = selected.getBoundingClientRect().top - viewport.getBoundingClientRect().top
    if (top < 0 || top + selected.offsetHeight > viewport.clientHeight) viewport.scrollTop += top - 45
  }, [open, opened.uid, exchange])

  const history = exchange?.entries ?? []
  const entries = history.some(entry => entry.uid === opened.uid) ? history
    : [...history, opened].sort((left, right) => (readDate(left.timestamp) ?? Infinity) - (readDate(right.timestamp) ?? Infinity))
  const codePath = exchange ? delegationSessionPath({ sessionId }) : undefined
  const onOpen = (href: string) => { onClose(); navigate(href) }
  const currentTitle = discussion.title
  const sessionTitle = exchange?.title || (exchange ? "Code session" : "Delegated session")
  return (
    <>
      <DialogHeader className="shrink-0 gap-3 border-b border-border-subtle px-4 pb-3 pt-3 sm:px-5 sm:pt-4" data-slot="delegation-exchange-header">
        <div className="flex min-w-0 items-center justify-between gap-2 pr-8">
          <DialogTitle className="m-0">Delegation exchange</DialogTitle>
          {navigation}
        </div>
        <div className="grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-2 sm:gap-3">
          <EntityCard className={cardClass} variant="outlined" current
            entity={{ id: discussion.id, typeSlug: "discussion", name: currentTitle }}
            visual={{ icon: "ph ph-chat-circle-dots" }}
            subtitle={["This discussion", discussion.agent].filter(Boolean).join(" · ")}
            action={{
              kind: "link", href: "/apps/nova/chat/" + encodeURIComponent(discussion.id),
              ariaLabel: "Open " + currentTitle, onClick: event => openLocally(event, onClose),
            }} />
          <i aria-hidden className="ph ph-arrows-left-right text-text-muted" />
          <EntityCard className={cardClass} variant="outlined"
            entity={{ id: sessionId, typeSlug: "ai-session", name: sessionTitle }}
            visual={{ icon: "ph ph-code" }}
            subtitle={exchange ? ["Code", exchange.repository].filter(Boolean).join(" · ") : undefined}
            action={codePath ? {
              kind: "link", href: codePath, ariaLabel: "Open " + sessionTitle + " in Code",
              onClick: event => openLocally(event, () => onOpen(codePath)),
            } : undefined} />
        </div>
      </DialogHeader>
      {queueStatus}
      <div ref={scroll} role="region" aria-label="Delegation activity" tabIndex={0}
        className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 [overflow-anchor:none] focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring sm:px-5">
        {load === "loading" && !exchange && <p role="status" className="mb-3 text-xs text-text-muted">Loading exchange…</p>}
        {load === "unavailable" && <p role="status" className="mb-3 text-xs text-text-muted">Code exchange unavailable.</p>}
        {load === "error" && (
          <div className="mb-3 flex items-center gap-2 text-xs text-text-muted">
            <span role="status">Exchange history unavailable.</span>
            <Button variant="ghost" size="xs" onClick={() => setAttempt(value => value + 1)}>Retry</Button>
          </div>
        )}
        <ol className="flex min-w-0 flex-col gap-4" aria-label="Delegation activity in chronological order">
          {entries.map((entry, index) => {
            const date = readDate(entry.timestamp)
            const day = date === undefined ? null : dayFormat.format(date)
            const previous = readDate(entries[index - 1]?.timestamp)
            const received = entry.kind === "update" || entry.kind === "reply"
            return (
              <li key={entry.uid} className="min-w-0">
                {day && (previous === undefined || dayFormat.format(previous) !== day) && (
                  <div className="mb-4 flex items-center gap-3 text-xs text-text-muted">
                    <span aria-hidden className="h-px flex-1 bg-overlay-10" />
                    <time dateTime={entry.timestamp}>{day}</time>
                    <span aria-hidden className="h-px flex-1 bg-overlay-10" />
                  </div>
                )}
                <Entry entry={entry} selected={entry.uid === opened.uid}
                  author={received ? exchange ? "Code" : undefined : discussion.agent}
                  codePath={codePath} onOpen={onOpen} />
              </li>
            )
          })}
        </ol>
      </div>
    </>
  )
}
