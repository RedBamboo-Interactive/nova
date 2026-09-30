# Recipient-bearing LIVE contributions

SDK PluginLiveEventProjection.Recipient is additive, preserving its four-argument
constructor/deconstruction. Recipient-less Presence uses its existing route unchanged.

Recipient projections require valid real owner-user/Agent GUIDs, bounded content,
source/metadata and an idempotency key. They route only to ONE existing LIVE with exact
owner_id/agent. Unknown, missing, closed, ambiguous, oversized or inaccessible targets
receive nothing: no global fallback, LocalDefault alias or discussion creation.
Scope/closure/uniqueness/disclosure are rechecked under the existing discussion write
gate before the idempotent injector persists. Unrelated creation of another discussion
after the final query is not globally serialized by this bounded repair.

Disclosure defaults to ConfidentialOnly. OwnerApprovedSummary is for trusted
contributors that independently obtained owner approval; it deliberately allows public
targets. Private contributors must use ConfidentialOnly and minimal public
invalidations with private retrieval. Confidential targets retain existing minimal
discussion.changed invalidations. Scoped keys are namespaced by user/Agent; durable
discussion dedup is unchanged. No transcript ownership/provider/queue/reconciliation
changes. Tests cover correct scope, concurrent retry, wrong user/Agent, unknown/closed/
ambiguous targets, disclosure policies, admission races and legacy Presence. Backend:
215 passed. Loaded-suite acceptance is separate.
