import { Badge, cn } from "@redbamboo/ui"
import { summaryPresentation, type DiscussionSummary } from "../../lib/discussion-summaries"

export function DiscussionSummaryCard({ summary }: { summary: DiscussionSummary }) {
  const { text } = summary
  const status = summaryPresentation[text.status]
  const source = summary.source === "fast" ? "Fast" : summary.source === "agent" ? "Agent" : "Edited"
  return (
    <section aria-label="Auto-summary" data-slot="discussion-auto-summary" className="rounded-lg border border-overlay-6 bg-overlay-3 px-3.5 py-3">
      <div className="mb-2 flex flex-wrap items-center gap-x-2 gap-y-1">
        <span className="inline-flex items-center gap-1.5 text-xs font-medium text-contrast">
          <i aria-hidden="true" className="ph-bold ph-list-bullets" /> Auto-summary
        </span>
        <Badge variant="outline" className={cn("border-current bg-overlay-4 text-[11px]", status.tone)}>
          <i aria-hidden="true" className={cn("ph-bold", status.icon)} />{status.label}
        </Badge>
        <time className="ms-auto text-[11px] text-text-muted" dateTime={summary.updatedAt}>
          {source} · {new Date(summary.updatedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
        </time>
      </div>
      <p className="text-sm font-medium text-contrast">{text.overview}</p>
      <dl className="mt-2 grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs leading-relaxed">
        <dt className="text-text-muted">What’s next</dt><dd>{text.nextStep ?? "Next step unconfirmed."}</dd>
        <dt className="text-text-muted">Waiting on you?</dt>
        <dd className={cn("font-medium", text.userAction ? status.tone : "text-contrast")}>
          {text.userAction ?? (text.status === "unclear" ? "No action identified." : "No action needed.")}
        </dd>
      </dl>
    </section>
  )
}
