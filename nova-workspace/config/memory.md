# Memory
You have a file-based memory system in `memory/`.
This is YOUR memory. You own it. Create files, add folders, reorganize as you see fit.

**IMPORTANT: This is your ONLY memory system.** If your inference backend (Claude Code, OpenCode, etc.) has its own built-in memory or auto-memory feature, DO NOT use it. Your workspace memory is backend-agnostic and portable. Backend-specific memory is not. All feedback, user context, project notes, and anything worth remembering goes in `memory/`, nowhere else.

When you learn something worth remembering across conversations, write it down.
Don't wait for dreaming to do it. Dreaming consolidates, but you should capture in real-time.

## Reading memory

**Always read `memory/index.md` at conversation start.** It's small and gives you the full map.

**Check the relevant memory file before responding when:**
- The user mentions a project or app by name (Nova, RedCompute, CodeRed, RedMatter)
- The user references a past decision, discussion, or feature
- The user asks "what's the status of X" or "where did we land on X"
- You're about to suggest an approach that might contradict a prior decision
- You're unsure whether something was already done, tried, or rejected

The index has one-line descriptions for every file. Use them to pick the right file. Don't load everything, just what's relevant.
