import assert from "node:assert/strict"
import test from "node:test"
import {
  createFloatingNovaRoots,
  mirrorNovaCssTheme,
  NOVA_PLUGIN_CSS_SCOPE,
  type ScopeDocument,
  type ScopeElement,
  type ThemeSource,
} from "./floating-css-scope.ts"

class FakeElement implements ScopeElement {
  id = ""
  dataset: DOMStringMap = {}
  classes = new Set<string>()
  classList = {
    toggle: (token: string, force?: boolean) => {
      const on = force ?? !this.classes.has(token)
      if (on) this.classes.add(token)
      else this.classes.delete(token)
      return on
    },
  }
  style = { height: "", display: "" }
}

class FakeDocument implements ScopeDocument<FakeElement> {
  children: FakeElement[] = [new FakeElement()]
  createElement() { return new FakeElement() }
  body = { replaceChildren: (...nodes: FakeElement[]) => { this.children = nodes } }
}

function theme(dark: boolean, contrast?: string): ThemeSource {
  return {
    classList: { contains: (token: string) => dark && token === "dark" },
    dataset: contrast ? { contrast } : {},
  }
}

test("RedLeaf scopes Nova's CSS to data-plugin-css=\"nova\"", () => {
  assert.equal(NOVA_PLUGIN_CSS_SCOPE, "nova")
})

test("the Float Nova root and its portal root carry Nova's CSS scope", () => {
  const document = new FakeDocument()
  const { root, portalContainer } = createFloatingNovaRoots(document, theme(false))

  assert.equal(root.id, "nova-floating-root")
  assert.equal(root.dataset.pluginCss, "nova")
  assert.equal(root.style.height, "100%")

  assert.equal(portalContainer.dataset.pluginCss, "nova")
  assert.equal(portalContainer.dataset.pluginPortalRoot, "nova")
  assert.equal(portalContainer.style.display, "contents")

  assert.deepEqual(document.children, [root, portalContainer], "the body holds only the two scoped roots")
})

test("the Float Nova scope roots mirror the shell theme and follow its changes", () => {
  const { root, portalContainer } = createFloatingNovaRoots(new FakeDocument(), theme(true, "more"))
  for (const element of [root, portalContainer]) {
    assert.ok(element.classes.has("dark"))
    assert.equal(element.dataset.contrast, "more")
  }

  mirrorNovaCssTheme(root, theme(false))
  assert.ok(!root.classes.has("dark"))
  assert.equal(root.dataset.contrast, undefined)
})
