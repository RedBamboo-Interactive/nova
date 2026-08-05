import { KeyCaptureInput, SettingRow, SectionHeader, Switch } from "@redbamboo/ui"
import { normalizePushToTalkKey, pushToTalkSettingsStore, usePushToTalkSettings } from "@redbamboo/chat"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()
  const pushToTalk = usePushToTalkSettings()

  return (
    <div>
      <SectionHeader>Appearance</SectionHeader>
      <SettingRow label="Show avatar">
        <Switch
          checked={localSettings.showAvatar}
          onCheckedChange={(v) => setLocalSettings({ showAvatar: v })}
        />
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
