import { Link } from "react-router-dom"
import type { DiscussionDelegationActivity } from "../lib/types"
import { delegationActivityLabel } from "../lib/delegation-activity"
import { delegationSessionPath } from "../lib/delegation-session-link"
import { BUILTIN_EVENTS } from "../lib/event-type-resolution"

// Older hosts have no accent-magenta token. Reuse the established delegation
// event color until the host supplies it, without expanding shared theme APIs.
const delegationColor = `var(--color-accent-magenta, ${BUILTIN_EVENTS.delegation!.color})`

export function DelegationSquare() {
  return <span data-slot="delegation-square" aria-hidden="true" className="inline-block size-2 shrink-0" style={{ backgroundColor: delegationColor }} />
}

export function DelegationCount({ activity }: { activity?: DiscussionDelegationActivity }) {
  if (!activity?.ongoingCount) return null
  const label = delegationActivityLabel(activity)
  return (
    <span data-slot="delegation-count" aria-label={label} title={label} className="inline-flex items-center gap-1 text-[10px] font-medium" style={{ color: delegationColor }}>
      <DelegationSquare />
      {activity.ongoingCount}
    </span>
  )
}

export function DelegationsInfo({ activity }: { activity?: DiscussionDelegationActivity }) {
  return (
    <section data-slot="delegations-info" aria-label="Ongoing delegations" className="rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2.5">
      <div className="flex items-center justify-between gap-3 text-xs font-medium text-contrast">
        <span>Delegations</span>
        <span>{activity ? `${activity.ongoingCount}${activity.unknownCount ? ` · ${activity.unknownCount} unknown` : ""}` : "—"}</span>
      </div>
      {activity && !activity.available && <p className="mt-1 text-[11px] text-text-muted">Status unavailable.{activity.ongoingCount > 0 ? " Last known work is retained." : ""}{activity.unknownCount > 0 ? " Unknown linked sessions are not counted as running." : ""}</p>}
      {!activity && <p className="mt-1 text-[11px] text-text-muted">Delegation status unavailable.</p>}
      {activity?.ongoingCount === 0 && activity.available && <p className="mt-1 text-[11px] text-text-muted">No ongoing delegations.</p>}
      {!!activity?.sessions.length && (
        <ul className="mt-2 space-y-2">
          {activity.sessions.map(session => (
            <li key={session.sessionId} data-delegation-session-id={session.sessionId} className="flex min-w-0 items-start gap-2">
              <span className="mt-1.5"><DelegationSquare /></span>
              <div className="min-w-0 flex-1">
                <Link to={delegationSessionPath({ sessionId: session.sessionId })!} className="block break-words text-xs text-contrast hover:underline">
                  {session.title || `Code session ${session.sessionId}`}
                </Link>
                <div className="mt-0.5 break-words text-[11px] text-text-muted">{session.repository || session.repositoryId || "Agent workspace"}</div>
              </div>
              <span className="shrink-0 text-[11px] text-text-muted">{session.status === "unavailable" ? "Status unavailable" : session.status === "running" ? "Running" : session.status === "queued" ? "Queued" : "Starting"}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
