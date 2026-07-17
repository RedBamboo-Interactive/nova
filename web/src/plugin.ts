import type { LeafAppPlugin } from "@redbamboo/utility"
import { NovaApp } from "./App"
import { NovaSettingsPanel } from "./components/NovaSettingsPanel"

export const plugin: LeafAppPlugin = {
  id: "nova",
  Page: NovaApp,
  settingsPanel: { Component: NovaSettingsPanel },
}
