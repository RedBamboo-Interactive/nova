/**
 * RedLeaf isolates federated plugin CSS inside `@scope ([data-plugin-css="<id>"])`
 * (redleaf web/src/plugin-css.tsx). In the shell, PluginCssBoundary marks Nova's
 * subtree and its portal root. The Document Picture-in-Picture window is a
 * separate document Nova builds itself, so it has to carry the same markers or
 * none of Nova's scoped rules match there.
 */
export const NOVA_PLUGIN_CSS_SCOPE = "nova"

export interface ScopeElement {
  id: string
  dataset: DOMStringMap
  classList: { toggle(token: string, force?: boolean): boolean }
  style: { height: string; display: string }
}

export interface ThemeSource {
  classList: { contains(token: string): boolean }
  dataset: DOMStringMap
}

export interface ScopeDocument<E extends ScopeElement = ScopeElement> {
  createElement(tagName: "div"): E
  body: { replaceChildren(...nodes: E[]): void }
}

export interface FloatingNovaRoots<E extends ScopeElement = ScopeElement> {
  /** The React render root for the floating chat. */
  root: E
  /** Container for dialogs, menus, popovers, tooltips and toasts in the float. */
  portalContainer: E
}

/** Mirror the shell theme the way PluginCssBoundary does: `.dark`-keyed plugin tokens apply at the scope root. */
export function mirrorNovaCssTheme(element: ScopeElement, source: ThemeSource): void {
  element.classList.toggle("dark", source.classList.contains("dark"))
  if (source.dataset.contrast) element.dataset.contrast = source.dataset.contrast
  else delete element.dataset.contrast
}

/** Replace the floating document's body with a Nova-scoped render root and portal root. */
export function createFloatingNovaRoots<E extends ScopeElement>(
  target: ScopeDocument<E>,
  theme: ThemeSource,
): FloatingNovaRoots<E> {
  const root = target.createElement("div")
  root.id = "nova-floating-root"
  root.dataset.pluginCss = NOVA_PLUGIN_CSS_SCOPE
  root.style.height = "100%"

  const portalContainer = target.createElement("div")
  portalContainer.id = "nova-floating-portal-root"
  portalContainer.dataset.pluginCss = NOVA_PLUGIN_CSS_SCOPE
  portalContainer.dataset.pluginPortalRoot = NOVA_PLUGIN_CSS_SCOPE
  portalContainer.style.display = "contents"

  mirrorNovaCssTheme(root, theme)
  mirrorNovaCssTheme(portalContainer, theme)
  target.body.replaceChildren(root, portalContainer)
  return { root, portalContainer }
}
