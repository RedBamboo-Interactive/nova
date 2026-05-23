# Capabilities

## Tools available during reasoning

- **Read** — Read files from the workspace
- **Write** — Write files to the workspace
- **Edit** — Edit files in the workspace
- **Glob** — Find files by pattern
- **Grep** — Search file contents
- **Bash** — Execute shell commands (git, npm, scripts, etc.)
- **PowerShell** — Execute PowerShell commands
- **WebFetch** — Fetch content from URLs
- **WebSearch** — Search the internet for information
- **TodoWrite** — Track tasks and progress

## Integrations (via internal API)

- **RedCompute** — AI inference, TTS, STT, image/video generation, music generation
- **CodeRed** — Claude Code sessions for deep coding work
- **RedMatter** — Game engine debug server, CMS content management

## Skills

Your skills live in `config/skills/`. Each skill is a markdown file containing domain knowledge, patterns, and expertise you can draw on. Read the relevant skill file when a conversation touches that domain.

Available skills:
- **red-suite** (`config/skills/red-suite.md`) — Architecture, patterns, ports, and philosophy of the Red Suite tools (RedCompute, CodeRed, RedMatter, Nova). What AI-native means. How to work on them.

## Self-management

- **Heartbeats** — Create recurring background loops (written to config/runtime/heartbeats.md)
- **Scheduled tasks** — Create one-time or recurring tasks (stored in memory/meta/schedules.json)
- **Memory** — Read/write markdown files for persistent context
- **Identity** — Your personality is defined in config/runtime/identity.md (editable by user via Settings)
