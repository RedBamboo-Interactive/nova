---
name: live-reports
description: "Report on several discussions at once as a nova-report block, which Nova renders as a structured card. Use whenever a message to your person covers more than one discussion or workstream: a catch-up, what happened while they were away, a summary of Orchestrated hand-offs."
---

When one message covers more than one discussion, put the facts in a `nova-report` block instead of paragraphs. Nova renders it as a card. Each row links its discussion and shows its workstream (the sidebar group), a state badge, your one sentence, and the commits and Code sessions the Journal recorded that day. Outside Nova the lines stay readable as they are.

```nova-report
title: While you were at German
since: 2026-11-25T09:20:00+01:00
needs 34ca9241: OpenCode only has a Cerebras credential here. | Add an Anthropic key | Keep Nova off OpenCode
done ad7278d0: The hotfix Nightly is out: RedLeaf 0.1.56 on Windows and Linux.
moving 8978cdde: The WinFsp relaunch is reworked after review and re-landing.
waiting 34ca9241 on the rebuild: The Codex MCP fix is ready and green.
repo redleaf: cb928f8f coordinates, 8978cdde writes.
```

- One line per discussion, keyed by its 8-character id: `done`, `moving`, `waiting <id> on <what>`, or `needs`.
- `needs` is only for what is truly theirs, after you have checked your operating rules and standing invitations. Put the answers after ` | `; they become buttons that reply for them.
- `waiting` always names what it waits on.
- `since:` is when the stretch began, as an ISO time with offset. It ties the Journal facts to the right day; without it the card shows no facts.
- `repo` lines say who coordinates and who writes a shared checkout, by discussion id.
- One sentence per row, in their language: what happened and why it matters, not the procedure. Leave commit hashes and session ids out; the card shows them.
- Open with a sentence or two of your own above the block. Personal and home things stay in your prose, outside the block.
