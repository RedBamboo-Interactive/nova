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
