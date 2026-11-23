import { useId } from "react"
import { KeyCaptureInput, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, SettingRow, SectionHeader, Switch } from "@redbamboo/ui"
import { composerFocusSettingsStore, normalizePushToTalkKey, pushToTalkSettingsStore, useComposerFocusSettings, usePushToTalkSettings, type ComposerAutoFocusMode } from "@redbamboo/chat"
import { useLocalSettings } from "../hooks/use-local-settings"
import { discussionGroupSortOptions, groupedDiscussionSortOptions, type DiscussionGroupSortMode, type GroupedDiscussionSortMode } from "../lib/discussion-grouping"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { VoiceDeviceSettings } from "./VoiceDeviceSettings"
import { FriezeMarkerSettings } from "./FriezeMarkerSettings"
import { normalizeFriezeDensity } from "../lib/frieze-marker-settings"

const WORK_STEP_OPTIONS = [["detailed", "Detailed"], ["quiet", "Quiet"], ["minimal", "Minimal"]] as const

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()
  const groupingHeadingId = useId()
  const workStepsName = useId()
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
        <SettingRow label="Group by project" hint="Nova uses titles and excerpts from the conversation to group open discussions in the sidebar by project or topic. Discussions without a clear match stay ungrouped.">
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
          <SettingRow label="Sort groups" hint="Choose how project headings are ordered. In Manual, drag a heading using its handle or use its move buttons.">
            <Select
              value={localSettings.discussionGroupSort}
              disabled={!localSettings.groupDiscussions}
              onValueChange={value => setLocalSettings({ discussionGroupSort: value as DiscussionGroupSortMode })}
            >
              <SelectTrigger className="w-40" aria-label="Sort groups">
                <SelectValue>{discussionGroupSortOptions.find(option => option.value === localSettings.discussionGroupSort)?.label}</SelectValue>
              </SelectTrigger>
              <SelectContent>
                {discussionGroupSortOptions.map(({ value, label }) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
              </SelectContent>
            </Select>
          </SettingRow>
          <SettingRow label="Sort discussions" hint="Choose the discussion order within each section of the grouped sidebar.">
            <Select
              value={localSettings.groupedDiscussionSort}
              disabled={!localSettings.groupDiscussions}
              onValueChange={value => setLocalSettings({ groupedDiscussionSort: value as GroupedDiscussionSortMode })}
            >
              <SelectTrigger className="w-40" aria-label="Sort discussions">
                <SelectValue>{groupedDiscussionSortOptions.find(option => option.value === localSettings.groupedDiscussionSort)?.label}</SelectValue>
              </SelectTrigger>
              <SelectContent>
                {groupedDiscussionSortOptions.map(({ value, label }) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
              </SelectContent>
            </Select>
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
      <SettingRow label="Work steps" hint="How much of Nova's work shows under each reply.">
        <div role="radiogroup" aria-label="Work steps" data-setting-path="friezeDensity"
          className="flex shrink-0 gap-0.5 rounded-lg bg-overlay-6 p-0.5">
          {WORK_STEP_OPTIONS.map(([value, label]) => (
            <label key={value} className="cursor-pointer">
              <input type="radio" name={workStepsName} value={value} className="peer sr-only"
                checked={normalizeFriezeDensity(localSettings.friezeDensity) === value}
                onChange={() => setLocalSettings({ friezeDensity: value })} />
              <span className="block rounded-md px-2 py-1 text-xs text-text-muted hover:text-text-primary peer-checked:bg-overlay-10 peer-checked:text-text-primary peer-focus-visible:outline-2 peer-focus-visible:outline-ring">
                {label}
              </span>
            </label>
          ))}
        </div>
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
