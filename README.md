# Nova (Leaf plugin)

AI companion chat for the Leaf kernel (RedLeaf) — multi-agent discussions,
delegation, and voice.

Nova exposes the `nova` capability and a declared `chat-avatar-overlay`
frontend slot. Optional experiences such as Outfits depend on that capability
and contribute UI through the slot without becoming part of Nova itself.

## Layout

- `plugin.json` — plugin manifest (id `nova`)
- `src/Leaf.Plugins.Nova/` — backend; references `Leaf.Sdk` only, never the kernel
- `web/` — frontend package `@redbamboo/plugin-nova` (exports a `LeafAppPlugin`)

## Building

This repo is consumed through the Leaf workspace: `leaf.workspace.json` in the
kernel repo (`redleaf`) lists this checkout's absolute path, and
`scripts/sync-workspace.ps1` junctions it to `plugins/nova` so the kernel
solution and web build pick it up. Through that junction the backend resolves
`Leaf.Sdk` at `..\..\..\..\src\Leaf.Sdk\Leaf.Sdk.csproj`; to build standalone,
pass `-p:LeafSdkProject=<path-to-Leaf.Sdk.csproj>`.

## Delegation recovery and verification

Authenticated next-message recovery preserves the current caller's app/user/Agent
identity and exact owner/provenance scope. It cannot override a manual stop.
Automatic Agent recovery uses fresh current authorization from RedLeaf.

Nova opts into durable prompt callbacks with explicit `promptMessageUid`, separate
from the logical HTTP `callbackId`. Legacy callbacks remain session-based.
Cancelled/superseded input reports that outcome and never completion. Definitively
rejected admission can remove its exact unaccepted preregistration through the
owner-authorized `/callback/unaccepted` operation; uncertain acceptance is retained.
Webhook delivery uses a bounded outbox with durable backoff and does not block input.

Delegation callers can optionally send `deploymentVerificationTarget: { service:
"redleaf" | "redcompute", runId: "<exact rebuild run>" }` to `POST
/api/apps/nova/delegate`. Nova forwards the typed target with the accepted prompt;
Compute waits for that exact canonical request and a consistent successful terminal
receipt before delivery. Pending, unknown, and failed targets remain visible in
delegation activity and Info. A newer deployment does not imply success or replacement
of an earlier one. Untyped delegation calls retain their existing behavior.
Explicit replacement uses the authenticated Compute endpoint `POST
/ai-session/sessions/{sessionId}/input-queue/{itemId}/supersede-verification`, with
`{ target: { service, runId }, replacement: { service, runId } }`. It cancels only
the matching never-delivered pending verification, retaining its terminal audit.
It neither admits a replacement prompt nor cancels ordinary queued work.

## Release candidate input

`release/producer-input.v1.json` defines the compact, channel-neutral release
producer. It intentionally blocks until the exact RedLeaf release-tool and
`Leaf.Sdk` commit pins are present. The current release tool resolves to RedLeaf
`9076a5791e79f368390ea4475c9c724a85ca6bea`; the SDK resolves to
`8a49a45cf4d80d354925d0c950b76a38b838486b`. It never signs trusted metadata.
The candidate derives only
`https://github.com/RedBamboo-Interactive/nova/releases/download/nova-unsigned-candidates/nova-<version>.leafpkg`.
The separate, serialized `nova-unsigned-candidates` prerelease bridge appends
only the candidate-ID-named unsigned descriptor and versioned `.leafpkg`, then
re-downloads and hashes both. It is unsigned acquisition plumbing, not a trust
boundary. Package staging is restricted to the manifest, published backend,
frontend `web/dist`, and declared application seeds/payload/provisioning; Nova
workspaces and user/private/generated state are excluded. From `web/`, use
`pnpm run typecheck`, `pnpm run build:pkg`, and `pnpm test` for the frontend
release checks.
