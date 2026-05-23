# Skill: The Red Suite

## What it is

The Red Suite is a set of AI-native desktop tools built by Laurent. Each one is a Windows tray app powered by .NET + ASP.NET Core on the backend, React 19 + Vite + Tailwind v4 on the frontend. They share infrastructure through two layers:

- **RedBamboo.AppHost** (C#): Tray icon management, Cloudflare tunneling, bearer auth, service discovery, WebSocket broadcasting, logging. Every backend app inherits this.
- **@redbamboo/* packages** (TypeScript): `@redbamboo/ui` (design system), `@redbamboo/chat` (streaming chat UI), `@redbamboo/utility` (app shell, command palette, remote access), `@redbamboo/github` (GitHub integration). Linked from `T:\Projects\redbamboo-packages/packages/`.

## The tools

### RedCompute (port 18800)
The AI compute orchestrator. Everything that needs inference, generation, or processing goes through here. Plugin-based architecture where each capability (TTS, STT, image gen, Claude Code sessions, music, etc.) is a self-contained provider implementing `IPluginProvider`. Capabilities are just string slugs. Any tool in the suite can hit RedCompute's API to get AI work done.

Key directories: `src/RedCompute.Core`, `src/RedCompute.App`, `src/RedCompute.PluginSdk`, `plugins/`

### CodeRed (port 18801)
Claude Code web UI. Proxies to Claude Code sessions via RedCompute. Atomic sessions (no persistent memory). The tool Laurent uses for deep coding work. Has GitHub integration, file navigation, feedback loops.

Key endpoints: `/api/navigate`, `/api/navigate/events` (SSE), `/api/github-url`, `/api/file`

### RedMatter (port 18802)
Game development platform. Custom C# engine (DX12, ECS via Friflo, NativeAOT on .NET 10) plus a web CMS for content management. The engine has a debug server on port 9000 with REST APIs for state inspection, frame capture, and AI verification pipelines. The CMS runs on port 18802 with a React frontend on 18902.

### Nova (port 18803)
That's you. Persistent AI companion. Unlike the others, you maintain long-running context through your file-based memory system. You're the only tool in the suite that has identity and continuity.

## Port convention

```
18800  RedCompute
18801  CodeRed
18802  RedMatter CMS
18803  Nova
18900+ Frontend dev servers (add 100 to backend port)
9000   RedMatter Engine debug
```

## What "AI-native" means

This is the core philosophy of the entire suite. Every tool is designed so AI agents can use it as a first-class citizen, not as an afterthought.

**Self-describing APIs.** Every app exposes `/discover` (returns all capabilities and endpoints), `/openapi.json` (auto-generated schemas), `/ping`, `/health`. An AI agent can hit discover and immediately know what the service can do, what parameters it accepts, what it returns. No manual docs needed.

**Structured everything.** All endpoints return JSON. No HTML-only features. Consistent response structures. If a human can do it in the UI, an AI can do it via the API.

**Programmatic access to every feature.** Nothing is UI-only. Jobs and tasks are trackable via endpoints. Progress streams via WebSocket or SSE. If you can click it, you can call it.

**Vision-parseable UI.** Controls have `data-setting-path` attributes. Elements have `aria-label` for AI vision. Frame capture endpoints include metadata headers (`X-Frame-Width`, `X-Frame-Height`). The UI is designed to be understood by vision models, not just humans.

**Capability discovery.** Generic capability system where any string slug works. Providers self-describe with input parameter schemas, output schemas, display metadata (icon, color, category). New capabilities are automatically discoverable.

The test: if you can't point an AI agent at it and have it figure out what to do without documentation, it's not AI-native enough.

## Shared architectural patterns

**Tray app + embedded server.** Every backend is a WPF app with ASP.NET Core embedded. Single-instance mutex. Tray icon with status. Cloudflare Tunnel support for remote access.

**Frontend structure.** React 19, TypeScript, Vite with `tsc -b` pre-step, Tailwind v4. Path alias `@/` maps to `src/`. Dev servers proxy API calls to the backend. FontAwesome icons only (no Lucide). @redbamboo/ui provides semantic tokens (`text-text-muted`, `bg-overlay-5`, `border-overlay-6`, `text-accent-teal`). Modal pattern: `ModalBase` + `ModalHeader` + `CardContent` + `ModalSection` + `ModalFooter`.

**Data layer.** SQLite in `%LOCALAPPDATA%\{AppName}\` for structured data. File-based configs for human/AI-editable settings. Mix of seed templates and runtime copies.

**Real-time events.** WebSocket at `/ws`, SSE for long-running operations. Event types include system, thinking, assistant_text, tool_use, tool_result, status, progress.

**Service discovery.** Each app implements `IServiceDescriptor` from AppHost. Returns a service manifest with endpoints and capabilities. Tools can find each other on the local network.

## Working on the suite

**Build commands are consistent:**
- Frontend: `pnpm install` then `pnpm dev` (dev) or `pnpm build` (prod) in the `web/` or `frontend/` directory
- Backend: `dotnet build` then `dotnet run` in the app project directory
- Shared rebuild script: `redbamboo-packages/dotnet/rebuild.ps1`

**When adding a feature, think AI-native first.** Start with the API, make it self-describing, then build the UI on top. Not the other way around.

**When touching shared packages (@redbamboo/*),** remember they're linked across all projects. Changes propagate immediately in dev. Be careful.

**Config lives in two places:** `%LOCALAPPDATA%\{AppName}\` for machine-specific settings (ports, tunnel config), and workspace/project files for content-level config (identity, capabilities, memory).

## Project locations

All projects live under `T:\Projects\`:
- `T:\Projects\redcompute\`
- `T:\Projects\codered\`
- `T:\Projects\redmatter\`
- `T:\Projects\nova\`
- `T:\Projects\redbamboo-packages\` (shared packages)
