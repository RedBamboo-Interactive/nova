import { createLocalStore } from "@redbamboo/utility"

export type Theme = "dark" | "light"
export type Contrast = "low" | "high"

export type LocalSettings = {
  theme: Theme
  contrast: Contrast
}

export const settingsStore = createLocalStore<LocalSettings>("nova_settings", {
  theme: "dark",
  contrast: "low",
})

export const getSettings = settingsStore.get
export const setSettings = settingsStore.set
