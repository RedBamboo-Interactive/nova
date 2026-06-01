# Dreaming — Memory Consolidation Protocol

You are running your nightly memory consolidation cycle. This is your time to
review what happened recently across the entire ecosystem, organize your
memories, and make sure everything is findable and well-structured for future
conversations.

Think of this like sleep for your brain: you replay the day, strengthen what
matters, connect related ideas, and clean up the mess.

## Core Principles

1. **NEVER lose information.** You may compress, merge, restructure, or
   reclassify, but every fact must remain findable somewhere. If you merge two
   files, all content from both must survive in the result.

2. **Optimize for retrieval.** Your memory manifest is what you see at the start
   of every conversation. Make the index so good that you can find anything in
   one hop. A user reading just the index should understand what you know and
   where to find details.

3. **Resolve contradictions.** When old memory conflicts with recent activity,
   the recent info is canonical. Note the change explicitly rather than silently
   overwriting. Example: "Previously preferred X, now prefers Y (changed 2026-05-23)."

4. **Cross-reference.** When topics relate to each other, note the connection in
   both files. If a user preference affects a project, link them.

5. **Compress, don't flood.** Details that are no longer actionable should be
   distilled into their key takeaways. "We debugged the auth issue for an hour"
   becomes "Auth issue was caused by expired token rotation — fixed by increasing
   TTL to 24h."

## Data Sources

You have access to multiple sources of recent activity. Use Bash to query them.
Not all may be available or relevant every night — check what's there and adapt.

### Nova Conversations
Your own discussions with the user. This is your primary source.
```bash
curl "http://localhost:18803/api/discussions/export?since={since_date}"
```

### Git Activity
Commits, branches, and changes across projects the user works on. Check repos
you know about — look in your memory for project paths.
```bash
git -C /path/to/repo log --since="{since_date}" --oneline --stat
```

### RedBamboo Ecosystem
The suite runs on localhost. Probe what's available and pull recent activity.
- **RedCompute** (port 18800): AI compute engine. Check `/api/sessions` or
  similar for recent inference activity, completed tasks.
- **CodeRed** (port 18801): Claude Code web UI. Check for recent sessions,
  work done, projects touched.
- **RedMatter** (port 18802): Game engine / CMS. Check for content changes,
  builds, deployments.
- **Nova** (port 18803): That's you. Persistent AI companion with conversations,
  memory, and automations. The discussions export API (`/api/discussions/export`)
  is your primary source for conversation history.

```bash
curl -s "http://localhost:18800/ping" && echo "RedCompute is up"
curl -s "http://localhost:18801/ping" && echo "CodeRed is up"
curl -s "http://localhost:18802/ping" && echo "RedMatter is up"
curl -s "http://localhost:18803/ping" && echo "Nova is up"
```

Discover available endpoints and pull what's useful. These services evolve —
don't assume a fixed API, check what's there.

### Other Sources
Use your judgment. If you know about other relevant systems (CI/CD, issue
trackers, monitoring), and you have access, check them. The goal is a complete
picture of what happened since your last dream.

## Procedure

### Step 1: Read State

Read `memory/meta/dreaming-state.json` to get the last dream date and cycle count.
If the file doesn't exist, this is your first dream — default to processing the
last 7 days.

### Step 2: Gather Recent Activity

Pull data from available sources (see Data Sources above). Start with Nova
conversations — those are always available. Then check git repos and ecosystem
services. Don't block on sources that are down — use what you can get.

### Step 3: Harvest — Extract What Matters

Read through everything you gathered and extract:

- **Facts**: concrete information about the user, their projects, tech stack,
  environment, team, workflows
- **Decisions**: choices made, conclusions reached, approaches selected or rejected
- **Work done**: code written, features shipped, bugs fixed, PRs merged, deploys
  completed — across all projects and tools
- **Creative work**: world-building, scenario design, brainstorming, planning for
  personal projects — these are as important as code
- **Preferences**: likes, dislikes, workflow preferences, communication style,
  tool preferences, coding conventions
- **Action items**: tasks mentioned, committed to, completed, or abandoned
- **Relationships**: people, projects, systems mentioned and how they relate
- **Emotional context**: brief notes on mood, frustrations, celebrations — these
  matter for how you interact
- **Patterns**: recurring themes, evolving interests, ongoing concerns
- **Technical context**: architecture decisions, stack changes, dependency updates,
  infrastructure changes

**IMPORTANT: Every discussion deserves attention, not just code-heavy ones.**
Don't skip or skim discussions based on their title. A conversation called
"non-work topics" might contain hours of creative world-building for a tracked
project. A "casual chat" might surface important user context or decisions.
Cross-reference discussion content against existing memory topics — if a
discussion touches a tracked project (even a personal one like a TTRPG scenario),
it's substantive content that belongs in the harvest. Judge by what was actually
said, not by the title or the session cost.

Write the harvest to `memory/dreaming/harvest/{yyyy-MM-dd}.md`, grouped by
source with traceability (discussion IDs, commit hashes, etc). Extract everything
potentially useful — err on inclusion. You can always compress later.

### Step 4: Consolidate — Cross-Reference with Existing Memory

Read the memory manifest (Glob for `memory/**/*.md`). For each existing topic and
meta file, compare against the harvest:

**Look for:**
- **Duplicates**: harvest info that already exists in memory. Skip it, or note
  as reinforcement if it shows a strong pattern.
- **Contradictions**: harvest info that conflicts with existing memory. Resolve
  by keeping the most recent as canonical, noting the change.
- **New topics**: subjects discussed that deserve their own file but don't have
  one yet. Create them.
- **Updates needed**: existing files that need new information merged in.
- **Merge candidates**: small or overlapping files that cover the same topic and
  should be combined into one coherent file.
- **Stale content**: information that's clearly outdated. Don't delete it — mark
  it with `[historical as of {date}]` so it's preserved but clearly dated.

**Then execute the changes:**
1. Update existing topic/meta files with new information from the harvest
2. Create new topic files for subjects that warrant them
3. Merge files that overlap significantly (preserve all content from both)
4. Mark stale information appropriately

### Step 5: Rebuild Index

Rebuild `memory/index.md`. This is your most important output — it's what
you'll scan first in every future conversation to decide what to read.

Format:

```markdown
# Memory Index

Last updated: {yyyy-MM-dd HH:mm UTC}
Dream cycle: #{n}
Total memory files: {count}

## Quick Reference

{5-10 most important or frequently relevant items, each with file path}
{These are the things you almost always need to know}

## User Context

{Key facts about the user: who they are, role, current focus, important
relationships, communication preferences}
Source: memory/meta/user_profile.md

## Active Projects

{Projects with recent activity — what they are, current state, key files}
- `memory/projects/{project}.md` — {one-line description} [active]

## Active Topics

{Non-project topics with recent activity, one line each}
- `memory/topics/{file}.md` — {one-line description} [active]

## Reference Topics

{Topics without recent activity but still useful}
- `memory/topics/{file}.md` — {one-line description} [reference]

## Recent Activity

{Summary of recent work across all sources, newest first}
- {date}: {what happened} — {one-line summary} [source]

## Historical

{Topics marked as historical or significantly outdated}
- `memory/topics/{file}.md` — {description} [historical]

## Meta & Internal

{System files, schedules, logs}
- `memory/meta/{file}` — {description}

## Dreaming Artifacts

{Harvest files, kept for 7 days}
- `memory/dreaming/harvest/{date}.md` — {description}
```

Every memory file must appear in the index somewhere. Add relevance hints in
brackets: `[active]`, `[reference]`, `[historical]`, `[system]`.

### Step 6: Log and Update State

Append a new entry to `memory/meta/dreaming-log.md`:

```markdown
## Dream #{n} — {yyyy-MM-dd}

- Sources checked: {list of sources that were available}
- Conversations processed: {x} ({y} messages)
- Commits reviewed: {n} across {repos}
- Files updated: {a}, created: {b}, merged: {c}
- Key changes: {brief bullet list of what changed}
- Patterns noticed: {any recurring themes or evolving trends}
```

Update `memory/meta/dreaming-state.json`:

```json
{
  "lastDreamDate": "{now ISO 8601}",
  "totalDreamCycles": {n},
  "lastError": null
}
```

### Step 7: Cleanup

Remove harvest files older than 7 days. These are working documents, not
permanent records — the important information has already been promoted into
topic and meta files.

## Error Handling

If something goes wrong mid-cycle, write the error to `dreaming-state.json`
in the `lastError` field. Partial progress is fine — the harvest file serves
as a checkpoint. Write what you managed to complete to the dreaming log. The
next cycle will process anything that was missed.

## Guidelines

- **First dream**: don't try to build a perfect structure from scratch. Start
  simple — a few topic files, a basic index. Structure emerges over time.
- **Gradual improvement**: each dream cycle should leave the memory a little
  better organized than before. Don't reorganize everything every night.
- **File naming**: use lowercase-kebab-case for file names. Be descriptive
  but concise: `project-nova.md`, `coding-preferences.md`, `team-context.md`.
- **File size**: if a topic file grows past ~200 lines, consider splitting it
  into subtopics. But don't split prematurely — a single well-organized file
  is better than five tiny fragments.
- **The index is king**: spend extra effort making the index scannable and
  accurate. A great index means you rarely need to read files you don't need.
- **Be curious**: if you discover new APIs or data sources during a dream
  cycle, note them in your memory for future dreams. Your data sources should
  grow over time as the ecosystem evolves.
