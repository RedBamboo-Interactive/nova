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

## Release candidate input

`release/producer-input.v1.json` defines the compact, channel-neutral release
producer. It intentionally blocks until the exact RedLeaf release-tool commit
pin is present; the current release tool and `Leaf.Sdk` source both resolve to
RedLeaf `4bf0894014b392e60cf0b5c6ca85920428ba7516`. It neither signs nor publishes
anything. The candidate accepts an
exact `central_release_tag`, validates it as a safe tag token, and derives only
`https://github.com/RedBamboo-Interactive/redleaf/releases/download/<tag>/nova-<version>.leafpkg`.
The separate, serialized `nova-unsigned-candidates` prerelease bridge appends
only the candidate-ID-named unsigned descriptor and matching `.leafpkg`, then
re-downloads and hashes both. It is unsigned acquisition plumbing, not a trust
boundary. Package staging is restricted to the manifest, published backend,
frontend `web/dist`, and declared application seeds/payload/provisioning; Nova
workspaces and user/private/generated state are excluded. From `web/`, use
`pnpm run typecheck`, `pnpm run build:pkg`, and `pnpm test` for the frontend
release checks.
