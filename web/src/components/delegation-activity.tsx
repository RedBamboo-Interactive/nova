import { Link } from "react-router-dom"
import { MorphSpinner } from "@redbamboo/chat"
import type { ActivityStatusGroup } from "@redbamboo/chat"
import type { DelegationSessionActivity, DiscussionDelegationActivity } from "../lib/types"
import { delegationActivityLabel, delegationSessionLabel } from "../lib/delegation-activity"
import { delegationSessionPath } from "../lib/delegation-session-link"
import { BUILTIN_EVENTS } from "../lib/event-type-resolution"

// Older hosts have no accent-magenta token. Reuse the established delegation
// event color until the host supplies it, without expanding shared theme APIs.
const delegationColor = `var(--color-accent-magenta, ${BUILTIN_EVENTS.delegation!.color})`

export function DelegationSpinner({ paused = false }: { paused?: boolean }) {
  return <span data-slot="delegation-spinner" aria-hidden="true" className="inline-flex shrink-0"><MorphSpinner color={delegationColor} paused={paused} /></span>
}

const isLive = (session: DelegationSessionActivity) => ["running", "queued", "starting"].includes(session.status)

/** One delegated Code session: what it is, where it runs, and its state. */
function DelegationSessionDetails({ session, onNavigate }: { session: DelegationSessionActivity; onNavigate: () => void }) {
  const path = delegationSessionPath({ sessionId: session.sessionId })
  return (
    <div data-slot="delegation-session-details" className="space-y-3">
      <div className="min-w-0">
        <p className="break-words text-sm text-contrast">{session.title || `Code session ${session.sessionId}`}</p>
        <p className="mt-0.5 break-words text-xs text-text-muted">{session.repository || session.repositoryId || "Agent workspace"}</p>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <span className={`rounded px-1.5 py-0.5 font-mono text-[10px] ${isLive(session) ? "bg-pink-500/15 text-pink-300" : "bg-overlay-6 text-text-muted"}`}>
          {delegationSessionLabel(session.status)}
        </span>
        {session.status === "unavailable" && session.lastKnownStatus && (
          <span className="text-[11px] text-text-muted">Last known: {delegationSessionLabel(session.lastKnownStatus).toLowerCase()}</span>
        )}
      </div>
      {(session.blockedReason || session.errorCode) && (
        <p className="break-words text-xs text-text-muted">{(session.blockedReason || session.errorCode)!.replaceAll("_", " ")}</p>
      )}
      {path && (
        <Link to={path} onClick={onNavigate}
          className="inline-flex items-center gap-1.5 rounded-md bg-overlay-6 px-2.5 py-1 text-xs text-text-muted transition-colors hover:bg-overlay-10 hover:text-contrast">
          <i aria-hidden="true" className="ph ph-code text-[11px]" />
          Open in Code
        </Link>
      )}
    </div>
  )
}

/**
 * Delegations as host work in the chat's one activity row: a circle per
 * delegated session (delegations are events, and events are circles), opening
 * its details in the shared dialog. Null when no delegation is shown.
 */
export function delegationActivityGroup(activity: DiscussionDelegationActivity | undefined): ActivityStatusGroup | null {
  if (!activity || (activity.ongoingCount === 0 && activity.unknownCount === 0)) return null
  return {
    label: delegationActivityLabel(activity),
    icon: BUILTIN_EVENTS.delegation!.icon ?? undefined,
    items: activity.sessions.map(session => ({
      key: session.sessionId,
      title: "Delegation",
      label: `${session.title || `Code session ${session.sessionId}`}: ${delegationSessionLabel(session.status).toLowerCase()}`,
      color: delegationColor,
      shape: "circle" as const,
      running: activity.available && isLive(session),
      renderDetails: (close: () => void) => <DelegationSessionDetails session={session} onNavigate={close} />,
    })),
  }
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
              <span className="mt-0.5"><DelegationSpinner paused={!["running", "queued", "starting"].includes(session.status)} /></span>
              <div className="min-w-0 flex-1">
                <Link to={delegationSessionPath({ sessionId: session.sessionId })!} className="block break-words text-xs text-contrast hover:underline">
                  {session.title || `Code session ${session.sessionId}`}
                </Link>
                <div className="mt-0.5 break-words text-[11px] text-text-muted">{session.repository || session.repositoryId || "Agent workspace"}</div>
                {session.errorCode && <div className="mt-0.5 break-words text-[11px] text-text-muted">{session.errorCode.replaceAll("_", " ")}</div>}
              </div>
              <span className="shrink-0 text-[11px] text-text-muted">{delegationSessionLabel(session.status)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
