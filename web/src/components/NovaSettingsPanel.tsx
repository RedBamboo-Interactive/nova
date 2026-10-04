import { KeyCaptureInput, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, SettingRow, SectionHeader, Switch } from "@redbamboo/ui"
import { composerFocusSettingsStore, normalizePushToTalkKey, pushToTalkSettingsStore, useComposerFocusSettings, usePushToTalkSettings, type ComposerAutoFocusMode } from "@redbamboo/chat"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()
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
      <SectionHeader>Chat</SectionHeader>
      <SettingRow label="Composer autofocus" hint="Choose when opening or switching a discussion moves focus to the message field.">
        <Select
          value={composerFocus.autoFocusMode}
          onValueChange={(value: unknown) => composerFocusSettingsStore.set({ autoFocusMode: value as ComposerAutoFocusMode })}
        >
          <SelectTrigger className="w-40">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="desktop-only">Desktop only</SelectItem>
            <SelectItem value="always">Always</SelectItem>
            <SelectItem value="never">Never</SelectItem>
          </SelectContent>
        </Select>
      </SettingRow>
      <SectionHeader>Voice</SectionHeader>
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
