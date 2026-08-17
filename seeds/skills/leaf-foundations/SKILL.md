---
name: leaf-foundations
description: "Current Leaf platform architecture and safe operating contract: RedLeaf, RedCompute, plugins, discovery, entities, assets, workspaces, signed execution identity, confidentiality, and events. Use when inspecting a Leaf installation, choosing an API, operating entities or plugins, or reasoning about cross-service behavior."
---

# Leaf foundations

## Product boundary

- Treat RedLeaf as the kernel, entity platform, plugin host, web shell, authentication boundary,
  setup surface, and update coordinator. Its canonical loopback origin is
  `http://127.0.0.1:18804`.
- Treat RedCompute as the separate provider-backed compute service for sessions and durable jobs.
  Its canonical loopback origin is `http://127.0.0.1:18800`.
- Treat Nova, Code, and other applications as RedLeaf plugins. Nova uses `/apps/nova`; Code keeps
  the compatibility ID and route `codered` and `/apps/codered`.
- Treat every non-core extension as optional. Inspect the installation before assuming it exists.
- Do not reconstruct current topology from historical ports, private installation paths, or memory.

## Discover before calling

Use this order:

1. `/ping` for process liveness only.
2. `/health` for service health.
3. `/discover` for capabilities, routes, management surfaces, and `authMode`.
4. `/openapi.json` for request and response schemas.
5. On RedLeaf, inspect `/api/plugins`, `/api/extensions`, `/api/setup/capabilities`,
   `/api/system/health`, and `/ws/schema` for installed shape and event contracts.

Do not interpret a blank route-level `auth` field or absent OpenAPI security declaration as public
when service-wide authentication is required. Plugin-owned `/api/apps/*` routes may not yet appear
in kernel discovery; verify the enabled plugin and inspect its installed manifest/current source
instead of probing guessed URLs.

Respect native transports. The shell is HTML; Assets and media are binary; selected operations use
SSE or WebSocket rather than JSON polling.

## Preserve execution identity

Require `REDLEAF_EXECUTION_TOKEN` for Agent-initiated operational reads and writes. Send it as an
Authorization bearer only to trusted loopback RedLeaf or RedCompute. Inspect the accepted identity
with `/auth/execution-context` when attribution is uncertain.

Never place the bearer in a URL, browser storage, logs, screenshots, persisted scratch, memory, or
a request to another origin. Never retry through unsigned localhost or `LocalDefault`. Execution
identity records app, actor, beneficiary, context, parent, trace, and expiry; it does not widen the
beneficiary's permissions. Do not combine it with legacy `X-Compute-Provenance`.

## Use Leaf data correctly

- Treat durable concepts as typed RedLeaf entities. Schemas are data; versions and authorization
  belong to the entity contract.
- Use records or streams for high-volume history and events.
- Upload user-visible media to Assets and preserve the exact returned URL.
- Treat Agent workspaces and harness files as entity-backed projections. Author Agents and Skills
  through their entities, never by editing read-only `.agents`, `.claude`, `AGENTS.md`, or
  `CLAUDE.md` projections.
- Use `REDLEAF_SCRATCH_DIR` for disposable files. Never encode a machine's physical drive layout in
  portable knowledge.

## Enforce confidentiality consistently

Confidentiality is authorization, not presentation metadata. Require the explicit human owner, or
the owning Agent acting for that same beneficiary. Deny anonymous and `LocalDefault` access. Apply
the same decision to reads, lists, search, versions, updates, delete, revert, secrets, streams,
sessions, jobs, payloads, outputs, and events. Conceal denied direct lookups and never treat a known
ID as authority. Only the explicit human owner may change confidentiality.

## Verify outcomes

For durable or cross-service work, verify semantic invariants: exact created ID, persisted state,
provenance, idempotent retry, cancellation, terminal events, negative authorization paths, and the
real user-facing projection. HTTP 200 or a successful build alone is not acceptance.
