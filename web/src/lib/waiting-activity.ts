import type { ActivityStatusGroup, PendingQuestion } from "@redbamboo/chat"

const WAITING_COLOR = "var(--color-amber-300, #fcd34d)"

/**
 * The shared chat draws its own session line only while a turn streams, and
 * Nova's renderStatusLine is only asked then. A turn parked on a question or an
 * approval is not streaming, so nothing outside the card said it was waiting.
 * Host activity is the one place the row still draws then: a still circle
 * (waiting work doesn't move) with the text the person needs to see.
 *
 * `delegations` (the row's other host work) keeps its shapes behind the waiting one.
 */
export function withWaitingActivity(
  delegations: ActivityStatusGroup | null,
  pending: Pick<PendingQuestion, "kind"> | null | undefined,
): ActivityStatusGroup | null {
  if (!pending) return delegations
  const permission = pending.kind === "permission"
  const label = permission ? "Waiting for your approval..." : "Waiting for your answer..."
  return {
    label,
    icon: permission ? "ph-bold ph-shield-warning" : "ph-bold ph-question",
    title: label,
    items: [
      {
        key: "pending-question",
        title: permission ? "Approval needed" : "Question",
        label,
        color: WAITING_COLOR,
        shape: "circle",
        running: false,
        renderDetails: () => null,
      },
      ...(delegations?.items ?? []),
    ],
  }
}
