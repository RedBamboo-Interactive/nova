import { useLayoutEffect, useMemo, useRef, useState, type MouseEvent } from "react"
import { Link } from "react-router-dom"
import { Badge, Collapsible, CollapsibleContent, CollapsibleTrigger, MediaViewer, cn } from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { getEntityHref } from "@redbamboo/utility"
import { delegationSessionPath } from "../../lib/delegation-session-link"
import {
  commitChip,
  discussionPath,
  firstFew,
  jobPath,
  journalBadge,
  journalBody,
  journalChipLinks,
  journalFailedCount,
  journalFailedJobs,
  journalFoldsDetails,
  journalIcon,
  journalTime,
  journalTone,
  journalWhen,
  safeJournalHref,
  sessionChip,
  shortId,
  type JournalChipLink,
  type JournalTone,
} from "../../lib/journal"
import type { JournalArtifact, JournalEntry } from "../../lib/journal-types"

export interface JournalCardContext {
  day: string
  timeZone: string
  resolveImageSrc: (src: string) => string | undefined
}

// Own work has the primary tint, a failure the destructive one; the rest stays quiet. All of it is
// the discussion row's card (`ChatView`'s confidential row: border-overlay-6, bg-overlay-3) recoloured.
const CARD_TONES: Record<JournalTone, string> = {
  own: "border-primary-a30 bg-primary-a10",
  failed: "border-destructive-a30 bg-destructive-a10",
  "with-you": "border-overlay-6 bg-overlay-3",
  scheduled: "border-overlay-6 bg-overlay-3",
  tick: "border-overlay-6 bg-overlay-3",
  running: "border-overlay-6 bg-overlay-3",
  unknown: "border-overlay-6 bg-overlay-3",
}

const ICON_TONES: Record<JournalTone, string> = {
  own: "text-primary",
  failed: "text-destructive",
  "with-you": "text-accent-teal",
  scheduled: "text-text-muted",
  tick: "text-accent-gold",
  running: "text-accent-teal",
  unknown: "text-text-muted",
}

export const JOURNAL_DOT_TONES: Record<JournalTone, string> = {
  own: "bg-primary",
  failed: "bg-destructive",
  "with-you": "bg-accent-teal",
  scheduled: "bg-text-muted",
  tick: "bg-accent-gold",
  running: "bg-accent-teal",
  unknown: "bg-text-disabled",
}

const BADGE_TONES: Record<JournalTone, string> = {
  own: "border-transparent bg-primary text-primary-foreground",
  failed: "border-destructive-a50 text-destructive",
  // The accents are too pale to read as words on a light theme; there the border carries the colour.
  "with-you": "border-accent-teal-a50 text-text-secondary dark:text-accent-teal",
  scheduled: "border-overlay-12 text-text-muted",
  tick: "border-accent-gold-a30 text-text-secondary dark:text-accent-gold",
  running: "border-accent-teal-a50 text-text-secondary dark:text-accent-teal",
  unknown: "border-overlay-12 text-text-muted",
}

const automationHref = (id: string) => getEntityHref("automation", id)
const sessionPath = (sessionId: string) => delegationSessionPath({ sessionId })

// Same shape as `WorktreeChips`, with room for a thumb.
const CHIP_CLASS = "inline-flex min-h-7 max-w-full items-center gap-1.5 rounded-md bg-overlay-6 px-2 py-1 text-[11px] text-text-secondary"

function JournalChip({ chip }: { chip: JournalChipLink }) {
  const content = (
    <>
      <i aria-hidden="true" className={cn("ph-bold shrink-0 text-[12px]", chip.icon)} />
      <span className={cn("min-w-0 truncate", chip.mono && "font-mono")}>{chip.label}</span>
    </>
  )
  const className = cn(CHIP_CLASS, chip.destructive && "text-destructive",
    (chip.to || chip.href) && "transition-colors hover:bg-overlay-10 hover:text-contrast")
  if (chip.to) return <Link to={chip.to} title={chip.title} className={className}>{content}</Link>
  if (chip.href) return <a href={chip.href} target="_blank" rel="noreferrer" title={chip.title} className={className}>{content}</a>
  return <span title={chip.title} className={className}>{content}</span>
}

/** The Agent's words, verbatim: the chat's serif and its renderer, three lines until tapped. */
function JournalText({ text, resolveImageSrc }: { text: string; resolveImageSrc: JournalCardContext["resolveImageSrc"] }) {
  const [expanded, setExpanded] = useState(false)
  const [overflowing, setOverflowing] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useLayoutEffect(() => {
    const node = ref.current
    if (node && !expanded) setOverflowing(node.scrollHeight > node.clientHeight + 1)
  }, [expanded, text])

  const onTap = (event: MouseEvent<HTMLDivElement>) => {
    if (!overflowing && !expanded) return
    const target = event.target as HTMLElement
    // A link is still a link, and selecting words isn't a tap.
    if (target.closest("a, button") || target.ownerDocument.getSelection()?.toString()) return
    setExpanded((value) => !value)
  }

  return (
    <div>
      <div
        ref={ref}
        data-slot="journal-text"
        onClick={onTap}
        className={cn(
          "min-w-0 break-words text-sm leading-relaxed font-serif markdown-body [&>:first-child]:mt-0 [&>:last-child]:mb-0",
          !expanded && "line-clamp-3",
          (overflowing || expanded) && "cursor-pointer",
        )}
      >
        <MarkdownRenderer content={text} resolveImageSrc={resolveImageSrc} />
      </div>
      {(overflowing || expanded) && (
        <button
          type="button"
          aria-expanded={expanded}
          onClick={() => setExpanded((value) => !value)}
          className="mt-0.5 min-h-6 text-[11px] text-text-muted transition-colors hover:text-contrast"
        >
          {expanded ? "Show less" : "Show all"}
        </button>
      )}
    </div>
  )
}

const VISIBLE_THUMBNAILS = 6

function JournalArtifacts({ artifacts, resolveImageSrc }: { artifacts: readonly JournalArtifact[]; resolveImageSrc: JournalCardContext["resolveImageSrc"] }) {
  const [viewing, setViewing] = useState<number | null>(null)
  const images = useMemo(() => artifacts.flatMap((artifact) => {
    const src = artifact.kind === "image" ? resolveImageSrc(artifact.target) : undefined
    return src ? [{ id: artifact.key, src, alt: artifact.title ?? "Image" }] : []
  }), [artifacts, resolveImageSrc])
  const files = artifacts.filter((artifact) => artifact.kind !== "image")
  if (images.length === 0 && files.length === 0) return null

  return (
    <>
      {images.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {images.slice(0, VISIBLE_THUMBNAILS).map((image, index) => (
            <button
              key={image.id}
              type="button"
              aria-label={`Open ${image.alt}`}
              onClick={() => setViewing(index)}
              className="relative h-14 w-20 overflow-hidden rounded-md border border-overlay-6 bg-overlay-6"
            >
              <img src={image.src} alt="" loading="lazy" decoding="async" className="h-full w-full object-cover" />
              {index === VISIBLE_THUMBNAILS - 1 && images.length > VISIBLE_THUMBNAILS && (
                <span className="absolute inset-0 flex items-center justify-center bg-overlay-30 text-xs font-medium text-contrast">
                  +{images.length - VISIBLE_THUMBNAILS}
                </span>
              )}
            </button>
          ))}
        </div>
      )}
      {files.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {files.map((file) => (
            <JournalChip key={file.key} chip={{
              key: file.key,
              label: file.title ?? file.target.split(/[\\/]/).pop() ?? "File",
              icon: "ph-file-text",
              to: null,
              href: safeJournalHref(resolveImageSrc(file.target)),
              title: file.target,
            }} />
          ))}
        </div>
      )}
      {viewing !== null && (
        <MediaViewer
          items={images}
          index={viewing}
          onIndexChange={setViewing}
          onClose={() => setViewing(null)}
          ariaLabel="Journal images"
        />
      )}
    </>
  )
}

/** What a busy card folds away: sessions, then commits, then what failed, first three of each. */
function JournalDetails({ entry }: { entry: JournalEntry }) {
  const [allSessions, setAllSessions] = useState(false)
  const [allCommits, setAllCommits] = useState(false)
  const [allJobs, setAllJobs] = useState(false)
  const sessions = firstFew(entry.sessions ?? [], allSessions)
  const commits = firstFew(entry.commits ?? [], allCommits)
  const failed = journalFailedJobs(entry)
  const jobs = firstFew(failed, allJobs)
  const sessionCount = entry.sessions?.length ?? 0

  const more = (hidden: number, show: () => void) => hidden > 0 && (
    <li>
      <button type="button" onClick={show} className="min-h-7 text-[12px] text-text-muted transition-colors hover:text-contrast">
        {hidden} more
      </button>
    </li>
  )

  return (
    <div className="flex flex-col gap-2 border-t border-overlay-6 pt-2 text-[12px]">
      {sessionCount > 0 && <div className="text-[11px] font-medium text-text-muted">{sessionCount} Code {sessionCount === 1 ? "session" : "sessions"}</div>}
      {(entry.sessions?.length ?? 0) > 0 && (
        <ul className="flex flex-col gap-1">
          {sessions.shown.map((session) => {
            const chip = sessionChip(session, sessionPath)
            return (
              <li key={session.sessionId} className="flex min-h-7 items-center gap-2">
                <i aria-hidden="true" className="ph-bold ph-code shrink-0 text-text-muted" />
                {chip.to ? (
                  <Link to={chip.to} className="min-w-0 flex-1 truncate text-contrast hover:underline">
                    {[session.repository, session.title].filter(Boolean).join(" · ") || "Session"}
                    <span className="ml-1.5 font-mono text-[11px] text-text-muted">{shortId(session.sessionId)}</span>
                  </Link>
                ) : (
                  <span className="min-w-0 flex-1 truncate">{session.title ?? session.sessionId}</span>
                )}
                <span className={cn("shrink-0 text-[11px]", session.status === "failed" ? "text-destructive" : "text-text-muted")}>{session.status}</span>
              </li>
            )
          })}
          {more(sessions.hidden, () => setAllSessions(true))}
        </ul>
      )}
      {(entry.commits?.length ?? 0) > 0 && (
        <div className="flex flex-col gap-1">
          <div className="text-[11px] font-medium text-text-muted">Commits</div>
          <ul className="flex flex-col gap-1">
            {commits.shown.map((commit) => {
              const chip = commitChip(commit)
              const words = (
                <>
                  {commit.message ?? commit.shortHash}
                  <span className="ml-1.5 font-mono text-[11px] text-text-muted">{commit.shortHash}</span>
                </>
              )
              return (
                <li key={commit.hash} className="flex min-h-7 items-center gap-2">
                  <i aria-hidden="true" className="ph-bold ph-git-commit shrink-0 text-text-muted" />
                  {chip.href
                    ? <a href={chip.href} target="_blank" rel="noreferrer" className="min-w-0 flex-1 truncate text-contrast hover:underline">{words}</a>
                    : <span className="min-w-0 flex-1 truncate">{words}</span>}
                  <span className="shrink-0 text-[11px] text-text-muted">{commit.repository}</span>
                </li>
              )
            })}
            {more(commits.hidden, () => setAllCommits(true))}
          </ul>
        </div>
      )}
      {failed.length > 0 && (
        <ul className="flex flex-col gap-1">
          {jobs.shown.map((job) => (
            <li key={job.id} className="flex min-h-7 items-center gap-2">
              <i aria-hidden="true" className="ph-bold ph-warning shrink-0 text-destructive" />
              <Link to={jobPath(job.id)} className="min-w-0 flex-1 truncate text-contrast hover:underline">
                {job.name ?? "Job"}
                <span className="ml-1.5 font-mono text-[11px] text-text-muted">{shortId(job.id)}</span>
              </Link>
              <span className="shrink-0 text-[11px] text-destructive">{job.status === "TimedOut" ? "timed out" : "failed"}</span>
            </li>
          ))}
          {more(jobs.hidden, () => setAllJobs(true))}
        </ul>
      )}
    </div>
  )
}

export function JournalEntryCard({ entry, context }: { entry: JournalEntry; context: JournalCardContext }) {
  const tone = journalTone(entry)
  const folds = journalFoldsDetails(entry)
  const [open, setOpen] = useState(false)
  const chips = journalChipLinks(entry, automationHref, sessionPath)
  const asked = entry.request
  const askedWhen = asked ? journalWhen(asked.at, context.day, context.timeZone) : null
  const started = entry.startedFrom
  const body = journalBody(entry)
  const failedCount = journalFailedCount(entry)
  const titleTo = entry.discussionId && entry.kind === "thread" ? discussionPath(entry.discussionId) : null

  const card = (
    <div
      data-slot="journal-card"
      data-tone={tone}
      className={cn("flex min-w-0 flex-1 flex-col gap-2 rounded-lg border px-3 py-2.5", CARD_TONES[tone])}
    >
      <div className="flex min-w-0 items-center gap-2">
        <i aria-hidden="true" className={cn("ph-bold shrink-0 text-sm", journalIcon(entry), ICON_TONES[tone])} />
        {titleTo ? (
          <Link to={titleTo} className="min-w-0 flex-1 truncate text-[13px] font-semibold text-contrast hover:underline">{entry.title}</Link>
        ) : (
          <span className="min-w-0 flex-1 truncate text-[13px] font-semibold text-contrast">{entry.title}</span>
        )}
        <Badge
          variant="outline"
          title={entry.inferred ? "Read from an older record that doesn't say this outright" : undefined}
          className={BADGE_TONES[tone]}
        >
          {journalBadge(entry)}
        </Badge>
        {folds && (
          <CollapsibleTrigger
            aria-label={open ? "Hide details" : "Show details"}
            className="-my-1 inline-flex size-7 shrink-0 items-center justify-center rounded-md text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast"
          >
            <i aria-hidden="true" className={cn("ph-bold", open ? "ph-caret-up" : "ph-caret-down")} />
          </CollapsibleTrigger>
        )}
      </div>

      {body && <JournalText text={body} resolveImageSrc={context.resolveImageSrc} />}

      {failedCount && <div data-slot="journal-failed-count" className="text-[12px] text-destructive">{failedCount}</div>}

      {asked && (
        <div className="min-w-0 text-[12px] text-text-muted">
          You asked{askedWhen ? ` ${askedWhen}` : ""}:{" "}
          <Link to={discussionPath(asked.discussionId, asked.messageUid)} className="underline decoration-dotted underline-offset-2 hover:text-contrast">
            {asked.quote ? `"${asked.quote}"` : "the message"}
          </Link>
        </div>
      )}
      {started && !asked && (
        <div className="min-w-0 truncate text-[12px] text-text-muted" title={started.inferred ? "Read from an older record that doesn't say this outright" : undefined}>
          Started from{" "}
          <Link to={discussionPath(started.discussionId)} className="underline decoration-dotted underline-offset-2 hover:text-contrast">
            {started.title ?? shortId(started.discussionId)}
          </Link>
        </div>
      )}

      {(entry.lines?.length ?? 0) > 0 && (
        <ul className="flex flex-col gap-0.5 text-[12px]">
          {entry.lines!.map((line) => (
            <li key={`${line.jobId}:${line.at}`} className="grid min-h-6 min-w-0 grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2.5">
              <time className="font-mono text-[11px] text-text-muted">{journalTime(line.at, context.timeZone)}</time>
              {line.automationId ? (
                <Link to={automationHref(line.automationId)} className="min-w-0 truncate text-contrast hover:underline">{line.title}</Link>
              ) : (
                <span className="min-w-0 truncate">{line.title}</span>
              )}
              {line.jobId ? (
                <Link to={jobPath(line.jobId)} className="font-mono text-[11px] text-text-muted hover:text-contrast hover:underline">
                  job {shortId(line.jobId)}
                </Link>
              ) : <span />}
              {line.text && (
                <div data-slot="journal-line-outcome" title={line.text} className="col-span-2 col-start-2 min-w-0 truncate text-[11px] text-text-muted">{line.text}</div>
              )}
            </li>
          ))}
        </ul>
      )}

      <JournalArtifacts artifacts={entry.artifacts ?? []} resolveImageSrc={context.resolveImageSrc} />

      {chips.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {chips.map((chip) => <JournalChip key={chip.key} chip={chip} />)}
        </div>
      )}

      {folds && (
        <CollapsibleContent>
          <JournalDetails entry={entry} />
        </CollapsibleContent>
      )}
    </div>
  )

  return folds ? (
    <Collapsible open={open} onOpenChange={setOpen} className="flex min-w-0 flex-1">{card}</Collapsible>
  ) : card
}
