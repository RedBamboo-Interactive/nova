import { createLocalStore } from "@redbamboo/utility"
import type { DiscussionGroupSortMode } from "./discussion-grouping"
import type { HiddenFriezeMarkers } from "./frieze-marker-settings"

export type LocalSettings = {
  showAvatar: boolean
  /** Agent avatars on discussion rows; off shows the plain status icon. */
  showDiscussionAvatars: boolean
  /** Optional automatic project sections in the open discussion list. */
  groupDiscussions: boolean
  refineDiscussionTitles: boolean
  useDiscussionThumbnails: boolean
  discussionGroupSort: DiscussionGroupSortMode
  discussionGroupOrder: string[]
  collapsedDiscussionGroups: (string | null)[]
  agentFilter: string | null
  lastUsedAgentId: string | null
  voiceInputDeviceId: string | null
  voiceInputDeviceLabel: string | null
  voiceOutputDeviceId: string | null
  voiceOutputDeviceLabel: string | null
  voiceProcessing: boolean
  /** Timeline marker kinds hidden in Live and in other discussions (display only). */
  hiddenFriezeMarkers: HiddenFriezeMarkers
  /** A small card in the agent message that started an image, video, music or workflow job. */
  jobCards: boolean
}

export const settingsStore = createLocalStore<LocalSettings>("nova_settings", {
  showAvatar: true,
  showDiscussionAvatars: true,
  groupDiscussions: false,
  refineDiscussionTitles: true,
  useDiscussionThumbnails: true,
  discussionGroupSort: "activity",
  discussionGroupOrder: [],
  collapsedDiscussionGroups: [],
  agentFilter: null,
  lastUsedAgentId: null,
  voiceInputDeviceId: null,
  voiceInputDeviceLabel: null,
  voiceOutputDeviceId: null,
  voiceOutputDeviceLabel: null,
  voiceProcessing: true,
  hiddenFriezeMarkers: { live: [], discussions: [] },
  jobCards: true,
})

export const getSettings = settingsStore.get
export const setSettings = settingsStore.set
