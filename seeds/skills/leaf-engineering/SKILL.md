---
name: leaf-engineering
description: "Engineering workflow for Leaf services and plugins: repository resolution, scratch discipline, API-first design, signed identity, source ownership, testing, real-surface acceptance, safe rebuilds, and deployment boundaries. Use when implementing, reviewing, debugging, testing, rebuilding, packaging, deploying, or releasing a Leaf change."
---

# Leaf engineering

## Resolve the real source

Resolve an existing checkout through Code's active `repository` entities and use its current
`local_path`. Match repository identity or remote, not a remembered folder. Read the target
repository's README, manifest, lockfiles, and scripts before choosing commands. Leaf has no universal
build recipe.

Preserve unrelated dirty work. Inspect status and overlapping diffs before editing. Never reset,
overwrite, commit, push, publish, rebuild shared infrastructure, or create a remote repository
without the authority appropriate to that separate action.

Put probes, downloads, generated scripts, screenshots, browser state, build staging, and temporary
artifacts under `REDLEAF_SCRATCH_DIR`. Promote only deliberate durable outputs.

## Trace the whole integration path

Before changing code, identify:

1. durable source and package ownership;
2. API and discovery contract;
3. authentication, authorization, confidentiality, and provenance;
4. job/event lifecycle and idempotency;
5. client state and real user surface;
6. persistence, restart, packaging, and deployment boundaries.

Start with the API. Make it discoverable, machine-readable, and testable, then build the UI on the
same contract. Stable IDs are not display names. Optional plugin behavior belongs to its owning
plugin rather than the kernel or an always-loaded Agent prompt.

## Keep credentials inside the suite boundary

Use the current `REDLEAF_EXECUTION_TOKEN` for operational RedLeaf and RedCompute calls. Never author
identity/provenance claims by hand, forward the bearer to another origin, or fall back to unsigned
localhost after rejection. Verify new work by exact returned job/session ID or `executionId`, never
by a global latest-item query.

## Implement defensively

- Prefer small contracts with explicit ownership over cross-product shortcuts.
- Validate inputs and negative paths at the boundary.
- Preserve idempotency across retries and use stable caller-supplied keys where supported.
- Keep source of truth singular. Generated projections and compiled package output are not authoring
  sources.
- Do not put mutable route catalogs, machine paths, provider choices, private IDs, or incident status
  into portable Skills.
- Treat user data and user-owned forks as runtime state. Package reconciliation may update
  package-owned templates but must not silently replace user selections or edits.

## Rebuild without destroying active work

Use the repository's checked-in `rebuild.ps1`. For a RedCompute-only change, run RedCompute's
script; for a RedLeaf or suite change, run RedLeaf's script. Use `-StageOnly` when deployment is
not authorized. Do not replace this path with Doctor, ad hoc file copies, or manual process kills.

An ordinary rebuild stages and validates first, then submits the staged request to RedCompute's
signed `/maintenance/deploy-staged` coordinator. The coordinator pauses queue delivery, crosses
per-session delivery locks, waits until no provider session is `Starting` or `Active`, writes the
planned-restart checkpoint, and only then hands promotion to the detached desktop process. A drain
timeout or unavailable coordinator must fail closed without stopping the suite.

Never use `-BootstrapMaintenance` during normal operation. It exists only for the one-time case
where an old loaded RedCompute predates the maintenance endpoint, and requires explicit coordination
because that bootstrap cannot preserve the initiating tool stream. Never force-stop a provider turn
to make a planned rebuild proceed.

After restart, preserve `maintenance_restart` and crash-recovered sessions as resumable; preserve an
explicit user stop as terminal. The next authenticated message owns provider resume and queued-input
delivery. Verify the terminal receipt, stable executable paths, loaded artifact hashes, both service
pings, and one real resumed conversation before claiming end-to-end continuity.

## Verify in layers

1. Run focused tests for the changed contract, including negative authorization cases.
2. Run the affected project's broader test/build/typecheck surface.
3. Inspect package contents when distribution changed.
4. Verify the backing API with exact IDs and persisted state.
5. Exercise the real RedLeaf route in Playwright when UI behavior changed.
6. Verify reload/restart behavior when durability matters.

Build and test offline when possible. A source build does not prove the running process loaded new
bytes. RedLeaf rebuild/restart, release publication, extension installation, remote creation, and
destructive data changes remain distinct authorization boundaries.
