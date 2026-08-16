import { createLocalStore } from "@redbamboo/utility"

export type LocalSettings = {
  showAvatar: boolean
  agentFilter: string | null
  lastUsedAgentId: string | null
}

export const settingsStore = createLocalStore<LocalSettings>("nova_settings", {
  showAvatar: true,
  agentFilter: null,
  lastUsedAgentId: null,
})

export const getSettings = settingsStore.get
export const setSettings = settingsStore.set
