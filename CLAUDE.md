# Nova — Persistent AI Companion

## What is Nova
Nova is a long-lived, identity-driven AI assistant that runs as a Windows tray app (PWA-capable).
Unlike CodeRed's atomic AI sessions, Nova maintains persistent context, has her own personality,
takes initiative via heartbeats/crons she sets up through conversation, and handles multiple concerns in parallel.

## Architecture
- **Backend**: .NET 9 WPF tray app with ASP.NET Core server on port **18803**
- **Frontend**: React 19 + Vite + Tailwind v4 PWA, using @redbamboo/ui, @redbamboo/chat, @redbamboo/utility
- **AI**: Claude via RedCompute (port 18800) — Nova is a client, not a Claude host
- **Memory**: File-based markdown in `nova-workspace/memory/` (manifest approach, not embedding)
- **Config**: `nova-workspace/config/` for identity, output protocol, capabilities
- **Data**: SQLite in `%LOCALAPPDATA%\Nova\` for structured data (schedules, conversation index)

## Project structure
```
nova/
  src/Nova.App/          # .NET WPF tray app + ASP.NET Core server
  web/                   # React frontend (PWA)
  nova-workspace/        # Runtime data
    config/              # Identity, protocols, capabilities (versioned seeds)
      seeds/             # Template configs (versioned)
      runtime/           # Active configs (gitignored, self-editable)
    memory/              # Conversations, topics, meta (gitignored)
```

## Port convention
- RedCompute: 18800
- CodeRed: 18801
- RedMatter: 18802
- **Nova: 18803**
- Frontend dev server: 18903

## Key patterns
- Uses RedBamboo.AppHost for tray icon, WebSocket, discovery, tunneling, auth
- ChatBackend adapter from @redbamboo/chat for the chat UI
- Server-persisted settings (identity editable from Settings panel)
- Heartbeats/crons are NOT pre-configured — Nova sets them up through conversation
- Memory uses manifest approach: Claude gets file paths, reads during reasoning
- Per-context request queues with global Claude semaphore

## Design system
- FontAwesome icons only (no Lucide)
- Use @redbamboo/ui semantic tokens (text-text-muted, bg-overlay-5, border-overlay-6, etc.)
- Modals: ModalBase + ModalHeader + CardContent + ModalSection + ModalFooter
- Dark theme default

## Commands
- `pnpm install` in `web/` for frontend deps
- `pnpm dev` in `web/` for frontend dev server (port 18903)
- `pnpm build` in `web/` to build frontend
- `dotnet build` in `src/Nova.App/` to build backend
- `dotnet run` in `src/Nova.App/` to run the app
