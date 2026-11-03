import { readFileSync } from "node:fs"
import assert from "node:assert/strict"
import { test } from "node:test"

test("registers the hidden Journal reader before the wildcard fallback", () => {
  const routesSource = readFileSync(new URL("./routes.tsx", import.meta.url), "utf8")
  const journalRoute = routesSource.indexOf('path: "journal/*"')
  const wildcardFallback = routesSource.indexOf('path: "*"')

  assert.notEqual(journalRoute, -1)
  assert.ok(journalRoute < wildcardFallback)
})

test("the Journal tab's route is the chat's own view and sits before the discussion id", () => {
  const routesSource = readFileSync(new URL("./routes.tsx", import.meta.url), "utf8")
  const journalRoute = routesSource.indexOf('path: "journal/:date?"')
  const discussionRoute = routesSource.indexOf('path: ":discussionId"')

  assert.notEqual(journalRoute, -1)
  assert.ok(journalRoute < discussionRoute)
  // The same element type at the same depth is what lets React keep the chat across the switch.
  assert.match(routesSource, /path: "journal\/:date\?",[\s\S]*?element: <ChatView \/>/)
  assert.match(routesSource, /path: ":discussionId",[\s\S]*?element: <ChatView \/>/)
})

test("the Markdown reader keeps its address and is called Memory on screen", () => {
  const routesSource = readFileSync(new URL("./routes.tsx", import.meta.url), "utf8")
  const panelSource = readFileSync(new URL("./panels/MemoryPanel.tsx", import.meta.url), "utf8")

  assert.match(routesSource, /path: "journal\/\*",[\s\S]*?element: <MemoryPanel \/>/)
  assert.match(panelSource, /<PanelHeader title="Memory">/)
})

test("registers the prefilled new-discussion route before the discussion id", () => {
  const routesSource = readFileSync(new URL("./routes.tsx", import.meta.url), "utf8")
  const newRoute = routesSource.indexOf('path: "new"')
  const discussionRoute = routesSource.indexOf('path: ":discussionId"')

  assert.notEqual(newRoute, -1)
  assert.ok(newRoute < discussionRoute)
  assert.match(routesSource, /path: "new",[\s\S]*?element: <NewDiscussionRoute \/>/)
})
