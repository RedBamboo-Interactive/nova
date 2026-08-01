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
