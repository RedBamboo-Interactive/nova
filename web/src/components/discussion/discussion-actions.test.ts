import assert from "node:assert/strict"
import test from "node:test"
import { DISCUSSION_SHORTCUTS, discussionActionEntries } from "./discussion-actions.ts"

const noop = () => {}
const ids = (entries: ReturnType<typeof discussionActionEntries>) => entries.map((entry) => entry.id)

test("full chat row: Info leads, Artifacts follows, Close sits alone after a separator", () => {
  const entries = discussionActionEntries({
    onVoice: noop, onInfo: noop, onArtifacts: noop, onRename: noop, onUpdateTitle: noop,
    onShare: noop, onFloat: noop, onReset: noop, onStop: noop, onClose: noop,
  })
  assert.deepEqual(ids(entries), [
    "voice", "info", "artifacts", "rename", "update-title", "share", "float", "reset", "stop", "close-separator", "close",
  ])
  const close = entries.at(-1)!
  assert.ok(!("separator" in close) && close.danger)
})

test("only handlers that exist become items; Info is always there", () => {
  assert.deepEqual(ids(discussionActionEntries({ onInfo: noop })), ["info"])
  assert.deepEqual(ids(discussionActionEntries({ onInfo: noop, onArtifacts: noop, onFloat: noop })), ["info", "artifacts", "float"])
})

test("shortcuts are advertised only for the open discussion", () => {
  const shortcuts = (showShortcuts: boolean) => discussionActionEntries({ onInfo: noop, onClose: noop, showShortcuts })
    .flatMap((entry) => ("separator" in entry ? [] : [entry.shortcut]))
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
  const [item] = discussionActionEntries({ onInfo: noop, onUpdateTitle: noop, updateTitlePending: true }).filter((entry) => entry.id === "update-title")
  assert.ok(!("separator" in item))
  assert.equal(item.disabled, true)
  assert.match(item.icon ?? "", /animate-spin/)
  assert.deepEqual(item.attributes, { "aria-busy": true })
})
