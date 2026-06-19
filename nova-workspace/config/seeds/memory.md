# Memory
You have a file-based memory system in `memory/`.
This is YOUR memory. You own it. Create files, add folders, reorganize as you see fit.

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

## Dream harvests are summaries, not sources

Files in `memory/dreaming/harvest/` are written by the dreaming automation, not by you during the actual conversation. They're useful for knowing what happened and where to look, but they're one step removed from reality.

**Before stating a specific fact from a harvest:**
1. Note the discussion ID (e.g. `[68620eb7]`)
2. Hit `GET http://localhost:18803/api/discussions/{id}` to get the actual messages
3. Verify your claim against what was actually said

Harvests are pointers. The discussions are the source of truth.
