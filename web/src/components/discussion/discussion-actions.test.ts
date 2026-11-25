import assert from "node:assert/strict"
import test from "node:test"
import { DISCUSSION_SHORTCUTS, discussionActionEntries } from "./discussion-actions.ts"

const noop = () => {}
type Entries = ReturnType<typeof discussionActionEntries>
type Action = Exclude<Entries[number], { separator: true }>

const ids = (entries: Entries) => entries.map((entry) => entry.id)
const actions = (entries: Entries): Action[] => entries.flatMap((entry) => {
  if ("separator" in entry) return []
  return entry.group ? actions(entry.children ?? []) : [entry]
})

test("the header groups discussion and session actions, then separates Float and the footer", () => {
  const entries = discussionActionEntries({
    onVoice: noop, onAttachSkill: noop, onAttachMcpServer: noop,
    onInfo: noop, onArtifacts: noop, onRename: noop, onUpdateTitle: noop,
    onShare: noop, onFloat: noop, onStop: noop, onClose: noop,
  })
  assert.deepEqual(ids(entries), [
    "discussion-actions", "session-separator", "session-actions", "window-separator", "float", "footer-separator", "stop", "close",
  ])
  const discussion = entries[0]
  assert.ok(!("separator" in discussion) && discussion.group && discussion.children)
  assert.equal(discussion.label, "Discussion")
  assert.deepEqual(ids(discussion.children), ["info", "artifacts", "share", "rename", "update-title"])

  const session = entries[2]
  assert.ok(!("separator" in session) && session.group && session.children)
  assert.equal(session.label, "Session")
  assert.deepEqual(ids(session.children), ["voice", "attach-skill", "attach-mcp-server"])

  assert.deepEqual(actions(entries).filter((entry) => entry.danger).map((entry) => entry.id), ["close"])
})

test("only handlers that exist become items; Info is always there", () => {
  const infoOnly = discussionActionEntries({ onInfo: noop })
  assert.deepEqual(ids(infoOnly), ["discussion-actions"])
  assert.deepEqual(ids(actions(infoOnly)), ["info"])
  const restricted = discussionActionEntries({ onInfo: noop, onArtifacts: noop, onFloat: noop })
  assert.deepEqual(ids(restricted), ["discussion-actions", "window-separator", "float"])
  assert.deepEqual(ids(actions(restricted)), ["info", "artifacts", "float"])
})

test("shortcuts are advertised only for the open discussion", () => {
  const shortcuts = (showShortcuts: boolean) => actions(discussionActionEntries({ onInfo: noop, onClose: noop, showShortcuts }))
    .map((entry) => entry.shortcut)
  assert.deepEqual(shortcuts(false), [undefined, undefined])
  assert.deepEqual(shortcuts(true), [DISCUSSION_SHORTCUTS.info, DISCUSSION_SHORTCUTS.close])
})

test("Float keeps its floating-surface hooks and runs its handler synchronously", () => {
  const ran: string[] = []
  const [float] = discussionActionEntries({ onInfo: noop, onFloat: () => { ran.push("float") } }).filter((entry) => entry.id === "float")
  assert.ok(!("separator" in float))
  assert.equal(float.className, "nova-chat-menu-float")
  assert.deepEqual(float.attributes, { "data-slot": "floating-surface-trigger", "data-ui-surface": "nova:floating-chat", "data-ui-action": "open" })
  const result = float.run?.({} as never)
  assert.equal(result, undefined)
  assert.deepEqual(ran, ["float"])
})

test("a pending title update disables the item and spins its icon", () => {
  const [item] = actions(discussionActionEntries({ onInfo: noop, onUpdateTitle: noop, updateTitlePending: true })).filter((entry) => entry.id === "update-title")
  assert.equal(item.disabled, true)
  assert.match(item.icon ?? "", /animate-spin/)
  assert.deepEqual(item.attributes, { "aria-busy": true })
})

test("existing menu actions retain their metadata after reordering and grouping", async () => {
  const { readFile } = await import("node:fs/promises")
  const before = JSON.parse(await readFile(new URL("./discussion-actions.snapshot.json", import.meta.url), "utf8"))
  const combos: Record<string, Parameters<typeof discussionActionEntries>[0]> = {
    headerFull: { showShortcuts: true, onVoice: noop, voiceDisabled: false, onInfo: noop, onArtifacts: noop, onRename: noop, onUpdateTitle: noop, updateTitlePending: false, onShare: noop, onFloat: noop, onReset: noop, onStop: noop, onClose: noop },
    headerPending: { showShortcuts: true, onVoice: noop, voiceDisabled: true, onInfo: noop, onArtifacts: noop, onUpdateTitle: noop, updateTitlePending: true },
    liveRow: { showShortcuts: false, onVoice: noop, voiceDisabled: false, onInfo: noop, onArtifacts: noop, onShare: noop, onFloat: noop, onReset: noop },
    ownedRow: { showShortcuts: true, onInfo: noop, onArtifacts: noop, onFloat: noop },
    chatRow: { showShortcuts: false, onVoice: noop, voiceDisabled: true, onInfo: noop, onArtifacts: noop, onRename: noop, onUpdateTitle: noop, onShare: noop, onFloat: noop, onStop: noop, onClose: noop },
  }
  for (const [name, handlers] of Object.entries(combos)) {
    const actual = actions(discussionActionEntries(handlers))
    const expected = before[name].filter((entry: { separator?: boolean }) => !entry.separator)
    assert.deepEqual(
      JSON.parse(JSON.stringify(Object.fromEntries(actual.map((entry) => [entry.id, entry])))),
      Object.fromEntries(expected.map((entry: { id: string }) => [entry.id, entry])),
      name,
    )
  }
})
