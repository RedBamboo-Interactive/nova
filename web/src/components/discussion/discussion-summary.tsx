import { useState } from "react"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, cn } from "@redbamboo/ui"
import {
  summaryDetails, summaryPresentation,
  type DiscussionSummary, type DiscussionSummaryView, type SummaryStatus,
} from "../../lib/discussion-summaries"

/**
 * The list row's status: a glyph only, its label carried by the accessible name and tooltip.
 * A glyph (not a dot) so it never reads as the unread dot or the avatar's activity badge.
 */
export function SummaryStatusIcon({ status, className }: { status: SummaryStatus; className?: string }) {
  const presentation = summaryPresentation[status]
  return (
    <span
      role="img"
      aria-label={presentation.label}
      title={presentation.label}
      data-slot="discussion-summary-status"
      data-summary-status={status}
      className={cn("inline-flex shrink-0 items-center text-[11px] leading-none", presentation.tone, className)}
    >
      <i aria-hidden="true" className={presentation.icon} />
    </span>
  )
}

function formatUpdated(iso: string) {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return ""
  const sameDay = at.toDateString() === new Date().toDateString()
  return sameDay
    ? at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : at.toLocaleString([], { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })
}

/** The modal's body: what is going on, what comes next, and what the reader has to do, if anything. */
export function DiscussionSummaryDetails({ summary, view, titleOf, onOpenDiscussion }: {
  summary: DiscussionSummary
  view?: DiscussionSummaryView | null
  titleOf?: (discussionId: string) => string | null | undefined
  onOpenDiscussion?: (discussionId: string) => void
}) {
  const details = summaryDetails(summary, view, titleOf)
  const presentation = summaryPresentation[details.status]
  return (
    <div data-slot="discussion-summary-details" className="flex flex-col gap-4 text-sm">
      {details.userAction && (
        <section aria-labelledby="summary-action" data-slot="discussion-summary-action"
          className="rounded-lg border border-accent-gold-a30 bg-accent-gold/10 px-3 py-2.5">
          <h3 id="summary-action" className="mb-1 text-xs font-semibold text-accent-gold">What you need to do</h3>
          <p className="font-medium text-contrast">{details.userAction}</p>
          {details.actionIn && (
            <p className="mt-1.5 text-xs text-text-muted">
              In{" "}
              {onOpenDiscussion ? (
                <button type="button" data-slot="discussion-summary-open-via"
                  onClick={() => onOpenDiscussion(details.actionIn!.discussionId)}
                  className="font-medium text-contrast underline underline-offset-2 hover:text-accent-gold focus-visible:outline-2 focus-visible:outline-primary rounded-sm">
                  {details.actionIn.title}
                </button>
              ) : <span className="font-medium text-contrast">{details.actionIn.title}</span>}
              , which this discussion is waiting on.
            </p>
          )}
        </section>
      )}
      <section aria-labelledby="summary-overview">
        <h3 id="summary-overview" className="mb-1 text-xs font-medium text-text-muted">Overview</h3>
        <p className="text-contrast">{details.overview}</p>
      </section>
      {details.waitingOnLabel && (
        <section aria-labelledby="summary-waiting-on">
          <h3 id="summary-waiting-on" className="mb-1 text-xs font-medium text-text-muted">Waiting on</h3>
          <p className="text-contrast">{details.waitingOnLabel}</p>
        </section>
      )}
      <section aria-labelledby="summary-next">
        <h3 id="summary-next" className="mb-1 text-xs font-medium text-text-muted">What’s next</h3>
        <p className="text-contrast">{details.nextStep ?? "Not confirmed yet."}</p>
      </section>
      <p className="flex items-center gap-1.5 text-xs text-text-muted">
        <i aria-hidden="true" className={cn(presentation.icon, presentation.tone)} />
        {presentation.label} · Updated <time dateTime={details.updatedAt}>{formatUpdated(details.updatedAt)}</time>
      </p>
    </div>
  )
}

/** The chip itself: icon and a short status word, announced as opening the summary. */
export function StatusChipButton({ status, expanded, onClick }: { status: SummaryStatus; expanded: boolean; onClick: () => void }) {
  const presentation = summaryPresentation[status]
  return (
    <button
      type="button"
      aria-haspopup="dialog"
      aria-expanded={expanded}
      aria-label={`Status: ${presentation.label}. Show summary`}
      title="Show summary"
      data-slot="discussion-status-chip"
      data-summary-status={status}
      onClick={onClick}
      className={cn(
        "inline-flex h-6 max-w-full items-center gap-1.5 rounded-full border px-2.5 text-xs transition-colors",
        "hover:bg-overlay-6 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary",
        presentation.chip,
      )}
    >
      <i aria-hidden="true" className={presentation.icon} />
      <span>{presentation.label}</span>
    </button>
  )
}

/** Inside a discussion: one compact chip; the full summary opens in a modal. */
export function DiscussionStatusChip({ summary, view, titleOf, onOpenDiscussion }: {
  summary: DiscussionSummary
  view?: DiscussionSummaryView | null
  titleOf?: (discussionId: string) => string | null | undefined
  onOpenDiscussion?: (discussionId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const presentation = summaryPresentation[summary.text.status]
  return (
    <>
      <StatusChipButton status={summary.text.status} expanded={open} onClick={() => setOpen(true)} />
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent motion="snappy" className="max-h-[85dvh] w-[calc(100vw-2rem)] max-w-md overflow-y-auto"
          data-slot="discussion-summary-dialog">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <i aria-hidden="true" className={cn(presentation.icon, presentation.tone)} />
              {presentation.label}
            </DialogTitle>
            <DialogDescription>{summary.text.label}</DialogDescription>
          </DialogHeader>
          <DiscussionSummaryDetails summary={summary} view={view} titleOf={titleOf}
            onOpenDiscussion={onOpenDiscussion ? id => { setOpen(false); onOpenDiscussion(id) } : undefined} />
        </DialogContent>
      </Dialog>
    </>
  )
}
