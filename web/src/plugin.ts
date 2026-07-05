import type { LeafAppPlugin } from "@redbamboo/utility"
import { NovaApp } from "./App"

export const plugin: LeafAppPlugin = {
  id: "nova",
  Page: NovaApp,
}
