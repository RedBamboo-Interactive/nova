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

test("registers the prefilled new-discussion route before the discussion id", () => {
  const routesSource = readFileSync(new URL("./routes.tsx", import.meta.url), "utf8")
  const newRoute = routesSource.indexOf('path: "new"')
  const discussionRoute = routesSource.indexOf('path: ":discussionId"')

  assert.notEqual(newRoute, -1)
  assert.ok(newRoute < discussionRoute)
  assert.match(routesSource, /path: "new",[\s\S]*?element: <NewDiscussionRoute \/>/)
})
