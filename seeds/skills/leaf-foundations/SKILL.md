---
name: leaf-foundations
description: "Current Leaf platform architecture and safe operating contract: RedLeaf, RedCompute, plugins and extensions, discovery, entities, assets, workspaces, signed execution identity, confidentiality, and events. Use when inspecting a Leaf installation, choosing an API, operating entities or plugins, reasoning about cross-service behavior, or answering what Leaf can do, what to install, why an extension is not working, or how to use one."
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

## Extensions

Leaf grows through optional extensions. Their `kind` says what installing one changes:

- `app`: adds a screen the user opens.
- `ability`: no new screen; Agents gain a power.
- `companion`: works in the background and feeds context.
- `integration`: links Leaf to an outside service or device.

Abilities in the `system` category are building blocks: shared foundations other extensions plug
into, installed automatically when needed. Do not suggest them on their own. An extension with no
guide has no kind yet.

Answer extension questions from these RedLeaf routes, with the execution bearer. `/discover` lists
them under its `extensions` capability. Never guess, and never read package source or manifests to
answer them.

- `GET /api/extensions/catalog?lang=<user's language>`: every extension Leaf knows, installed or
  not, with `kind`, `category`, `tagline`, `installed`, `readiness { state, unmetCount, nextAction }`,
  `whenToSuggest`, `agentSkill` (the Skill the extension gives Agents), and `detailPath`. It is
  small; read it whole and filter it yourself.
- `GET /api/extensions/{id}/guide?lang=<user's language>`: what it does, requirements, setup, use
  cases with `try` prompts, limits, troubleshooting, and `agent.whenToSuggest`. A 404
  `guide_not_found` means there is no guide; say so instead of filling the gap.
- `GET /api/extensions/{id}/readiness`: whether it works on this PC now (`ready`, `needs-setup`,
  `degraded`, `unsupported`, `installable`), with evidence per requirement and the next action.

- What can Leaf do, or what should I install for X: match the request against catalog taglines,
  kinds and categories, then read the guide of the best one or two. Suggest one only when its
  `whenToSuggest` fits, and check its readiness so you can say whether it will work on this PC.
- Why isn't X working: read its readiness; give the unmet item, its evidence and the next action,
  then the matching troubleshooting entry from its guide.
- How do I use X: answer from its guide's use cases and setup steps.
- Quote `try` prompts and `whenToSuggest` exactly as written; do not turn them into promises the
  guide does not make.

A private discussion's context may name installed extensions that need attention. Treat that line
as a pointer and read readiness before diagnosing.

The user installs; an Agent never installs or uninstalls an extension. To propose one, name it, say
why, and say what it needs and costs according to its guide. Then give the user its `detailPath`,
`/extensions/<id>`: it opens the Marketplace page with Install for an extension that is not
installed, and its own settings and guide once it is.

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
