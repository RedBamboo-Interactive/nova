import test from "node:test"
import assert from "node:assert/strict"
import {
  PendingVisibleContextStore,
  applyPendingVisibleContext,
  type PendingVisibleContextEntry,
} from "./pending-visible-context-store.ts"

function entry(app: string, discarded: string[]): PendingVisibleContextEntry {
  return {
    context: { app, url: `/${app}` },
    discard: () => discarded.push(app),
  }
}

test("pending visible context is isolated by discussion", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("CodeRed", discarded))
  store.set("b", entry("RedLeaf", discarded))

  assert.equal(store.get("a")?.context.app, "CodeRed")
  assert.equal(store.get("b")?.context.app, "RedLeaf")
  assert.equal(store.get("missing"), null)
  assert.deepEqual(discarded, [])
})

test("consume transfers attachment ownership without discarding it", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("CodeRed", discarded))

  assert.equal(store.consume("a")?.context.app, "CodeRed")
  assert.equal(store.get("a"), null)
  assert.deepEqual(discarded, [])
})

test("replace, dismiss, and runtime disposal clean abandoned captures", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("first", discarded))
  store.set("a", entry("second", discarded))
  store.set("b", entry("third", discarded))
  assert.deepEqual(discarded, ["first"])

  store.clear("a")
  assert.deepEqual(discarded, ["first", "second"])
  store.dispose()
  assert.deepEqual(discarded, ["first", "second", "third"])
})

test("text and image turns keep the captured screenshot inline", () => {
  let discarded = 0
  const screenshot = { mediaType: "image/png" as const, base64: "context" }
  const ownImage = { mediaType: "image/jpeg" as const, base64: "message" }
  const prepared = applyPendingVisibleContext(
    { content: "<nova-context>CodeRed</nova-context>\nExplain this", images: [ownImage] },
    {
      context: { app: "CodeRed", url: "/apps/codered", screenshot },
      screenshotAttachment: {
        id: "staged-context",
        kind: "image",
        name: "what-i-see.png",
        mediaType: "image/png",
        size: 7,
        downloadUrl: "/attachments/staged-context",
      },
      discard: () => { discarded += 1 },
    },
  )

  assert.match(prepared.content, /<nova-context>CodeRed<\/nova-context>/)
  assert.deepEqual(prepared.images, [screenshot, ownImage])
  assert.equal(prepared.attachments, undefined)
  assert.equal(discarded, 1)
})

test("file turns use the staged screenshot attachment", () => {
  let discarded = 0
  const screenshotAttachment = {
    id: "staged-context",
    kind: "image" as const,
    name: "what-i-see.png",
    mediaType: "image/png",
    size: 7,
    downloadUrl: "/attachments/staged-context",
  }
  const fileAttachment = {
    id: "user-file",
    kind: "file" as const,
    name: "trace.txt",
    mediaType: "text/plain",
    size: 5,
    downloadUrl: "/attachments/user-file",
  }
  const prepared = applyPendingVisibleContext(
    { content: "Explain this", attachments: [fileAttachment] },
    {
      context: {
        app: "CodeRed",
        url: "/apps/codered",
        screenshot: { mediaType: "image/png", base64: "context" },
      },
      screenshotAttachment,
      discard: () => { discarded += 1 },
    },
  )

  assert.deepEqual(prepared.attachments, [screenshotAttachment, fileAttachment])
  assert.equal(prepared.images, undefined)
  assert.equal(discarded, 0)
})
