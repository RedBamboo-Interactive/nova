import { test } from "node:test"
import assert from "node:assert/strict"
import {
  ASK_NOVA_PROVIDERS,
  ASK_NOVA_ITEMS,
  askNovaEntries,
  entityDraft,
  imageDraft,
  newDiscussionRoute,
  selectionDraft,
} from "./ask-nova-actions.ts"
import { MAX_LINKED_DRAFT_LENGTH, parseNewDiscussionRequest } from "./new-discussion-draft.ts"

const ORIGIN = "http://127.0.0.1:18804"

/** What the new-discussion route will put in the composer for an item. */
function draftOf(entries: ReturnType<typeof askNovaEntries>): string | null {
  const item = entries[0] as { run?: (ctx: unknown) => void } | undefined
  if (!item?.run) return null
  let route = ""
  item.run({ navigate: (to: string) => { route = to } })
  assert.match(route, /^\/apps\/nova\/chat\/new\?draft=/)
  return parseNewDiscussionRequest(route.slice(route.indexOf("?"))).draft
}

test("an entity becomes a standalone redleaf:// card link", () => {
  assert.equal(entityDraft({ id: "e 1", typeSlug: "note", name: "Plan [v2]" }), "[Plan \\[v2\\]](redleaf://note/e%201)")
  assert.equal(entityDraft({ id: "e1", typeSlug: "note" }), "[note](redleaf://note/e1)")
  assert.equal(entityDraft({ id: "", typeSlug: "note" }), null)
})

test("an image is referenced by address, same-origin by path", () => {
  assert.equal(imageDraft(`${ORIGIN}/api/assets/a b(1).png?v=2`, ORIGIN), "![image](/api/assets/a%20b%281%29.png?v=2)")
  assert.equal(imageDraft("https://example.invalid/x.jpg", ORIGIN), "![image](https://example.invalid/x.jpg)")
  assert.equal(imageDraft("data:image/png;base64,AAAA", ORIGIN), null)
  assert.equal(imageDraft("blob:http://127.0.0.1:18804/1234", ORIGIN), null)
})

test("a selection becomes a Markdown quote that survives the draft link", () => {
  assert.equal(selectionDraft("  first\r\n\r\nsecond  \n"), "> first\n>\n> second")
  assert.equal(selectionDraft(" \n "), null)
  const long = selectionDraft("x".repeat(MAX_LINKED_DRAFT_LENGTH * 2))!
  assert.equal(Array.from(long).length, MAX_LINKED_DRAFT_LENGTH)
  assert.ok(long.endsWith("…"))
})

test("the item opens a new discussion with the reference unsent", () => {
  assert.deepEqual(askNovaEntries("selection", null), [])
  const entries = askNovaEntries("selection", "> quoted\n>\n> text")
  assert.equal(entries.length, 1)
  assert.equal((entries[0] as { id: string }).id, "ask-nova-selection")
  assert.equal((entries[0] as { label?: string }).label, "Ask Nova about the selection")
  assert.equal(draftOf(entries), "> quoted\n>\n> text")
  assert.equal(newDiscussionRoute("a&b"), "/apps/nova/chat/new?draft=a%26b")
})

test("each kind says what it is about", () => {
  const labels = Object.values(ASK_NOVA_ITEMS).map(item => item.label)
  assert.equal(new Set(labels).size, labels.length)
  assert.equal(ASK_NOVA_ITEMS.entity.label, "Ask Nova about this")
})

test("providers contribute to entity, image and selection, never owning a kind", () => {
  assert.deepEqual(ASK_NOVA_PROVIDERS.map(provider => provider.kind), ["entity", "image", "selection"])
  assert.ok(ASK_NOVA_PROVIDERS.every(provider => provider.match === undefined))
  const ctx = { window: { location: { origin: ORIGIN } } }
  const [entity, image, selection] = ASK_NOVA_PROVIDERS
  const idsOf = (entries: unknown) => (entries as Array<{ id: string }>).map(entry => entry.id)
  assert.deepEqual(idsOf(entity.getItems({ id: "e1", typeSlug: "note" }, ctx as never)), ["ask-nova-entity"])
  assert.deepEqual(idsOf(image.getItems({ src: "/api/assets/a.png" }, ctx as never)), ["ask-nova-image"])
  assert.equal(draftOf(entity.getItems({ id: "e1", typeSlug: "note", name: "Plan" }, ctx as never) as never), "[Plan](redleaf://note/e1)")
  assert.equal(draftOf(image.getItems({ src: `${ORIGIN}/api/assets/a.png` }, ctx as never) as never), "![image](/api/assets/a.png)")
  assert.deepEqual(image.getItems({ src: "data:image/png;base64,AAAA" }, ctx as never), [])
  assert.equal(draftOf(selection.getItems({ text: "hello" }, ctx as never) as never), "> hello")
})
