import { useId } from "react"
import { KeyCaptureInput, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, SettingRow, SectionHeader, Switch } from "@redbamboo/ui"
import { composerFocusSettingsStore, normalizePushToTalkKey, pushToTalkSettingsStore, useComposerFocusSettings, usePushToTalkSettings, type ComposerAutoFocusMode } from "@redbamboo/chat"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { VoiceDeviceSettings } from "./VoiceDeviceSettings"
import { FriezeMarkerSettings } from "./FriezeMarkerSettings"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()
  const groupingHeadingId = useId()
  const pushToTalk = usePushToTalkSettings()
  const composerFocus = useComposerFocusSettings()

  return (
    <div>
      <SectionHeader>Appearance</SectionHeader>
      <SettingRow label="Show avatar">
        <Switch
          checked={localSettings.showAvatar}
          onCheckedChange={(v) => setLocalSettings({ showAvatar: v })}
        />
      </SettingRow>
      <SettingRow label="Agent avatars in discussions" hint="Show each discussion's Agent avatar in the list. When off, rows show a status icon instead. The Live quota border stays either way.">
        <Switch
          checked={localSettings.showDiscussionAvatars}
          onCheckedChange={(v) => setLocalSettings({ showDiscussionAvatars: v })}
        />
      </SettingRow>
      <div role="group" aria-labelledby={groupingHeadingId} className="mb-4 mt-2 rounded-lg bg-overlay-4 px-3 py-2.5">
        <h3 id={groupingHeadingId} className="text-sm font-medium text-contrast">Grouping</h3>
        <SettingRow label="Group by project" hint="Nova uses titles and opening messages to group open discussions in the sidebar by project or topic. Discussions without a clear match stay ungrouped.">
          <Switch
            aria-label="Group by project"
            checked={localSettings.groupDiscussions}
            onCheckedChange={(groupDiscussions) => setLocalSettings({ groupDiscussions })}
          />
        </SettingRow>
        <div className="ms-2 border-s border-overlay-10 ps-3">
          <SettingRow label="Refine titles" hint="Show concise sidebar labels for grouped discussions. Turn this off to see their full titles.">
            <Switch
              aria-label="Refine titles"
              checked={localSettings.refineDiscussionTitles}
              disabled={!localSettings.groupDiscussions}
              onCheckedChange={(refineDiscussionTitles) => setLocalSettings({ refineDiscussionTitles })}
            />
          </SettingRow>
          <SettingRow label="Use thumbnail images" hint="Show image previews for discussions and project headings when available. Turn this off to use icons.">
            <Switch
              aria-label="Use thumbnail images"
              checked={localSettings.useDiscussionThumbnails}
              disabled={!localSettings.groupDiscussions}
              onCheckedChange={(useDiscussionThumbnails) => setLocalSettings({ useDiscussionThumbnails })}
            />
          </SettingRow>
        </div>
      </div>
      <SectionHeader>Chat</SectionHeader>
      <SettingRow label="Composer autofocus" hint="Choose when opening or switching a discussion moves focus to the message field.">
        <Select
          value={composerFocus.autoFocusMode}
          onValueChange={(value: unknown) => composerFocusSettingsStore.set({ autoFocusMode: value as ComposerAutoFocusMode })}
        >
          <SelectTrigger className="w-40">
            <SelectValue>
              {composerFocus.autoFocusMode === "desktop-only" ? "Desktop only" : composerFocus.autoFocusMode === "always" ? "Always" : "Never"}
            </SelectValue>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="desktop-only">Desktop only</SelectItem>
            <SelectItem value="always">Always</SelectItem>
            <SelectItem value="never">Never</SelectItem>
          </SelectContent>
        </Select>
      </SettingRow>
      <FriezeMarkerSettings />
      <SettingRow label="Job cards" hint="Show a small card with progress in the agent message that started an image, video, music or workflow job.">
        <Switch
          checked={localSettings.jobCards}
          onCheckedChange={(v) => setLocalSettings({ jobCards: v })}
        />
      </SettingRow>
      <SectionHeader>Voice</SectionHeader>
      <VoiceDeviceSettings />
      <SettingRow label="Push-to-talk key" hint="Hold this key to record a voice reply. Float Nova leases the same key globally while open.">
        <KeyCaptureInput
          value={pushToTalk.key}
          onChange={(key) => pushToTalkSettingsStore.set({ key })}
          normalizeKey={normalizePushToTalkKey}
        />
      </SettingRow>
    </div>
  )
}
