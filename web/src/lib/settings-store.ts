import { createLocalStore } from "@redbamboo/utility"

export type LocalSettings = {
  showAvatar: boolean
  /** Agent avatars on discussion rows; off shows the plain status icon. */
  showDiscussionAvatars: boolean
  agentFilter: string | null
  lastUsedAgentId: string | null
  voiceInputDeviceId: string | null
  voiceInputDeviceLabel: string | null
  voiceOutputDeviceId: string | null
  voiceOutputDeviceLabel: string | null
  voiceProcessing: boolean
}

export const settingsStore = createLocalStore<LocalSettings>("nova_settings", {
  showAvatar: true,
  showDiscussionAvatars: true,
  agentFilter: null,
  lastUsedAgentId: null,
  voiceInputDeviceId: null,
  voiceInputDeviceLabel: null,
  voiceOutputDeviceId: null,
  voiceOutputDeviceLabel: null,
  voiceProcessing: true,
})

export const getSettings = settingsStore.get
export const setSettings = settingsStore.set
