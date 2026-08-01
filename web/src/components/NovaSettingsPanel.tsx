import { SettingRow, SectionHeader, Switch } from "@redbamboo/ui"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()

  return (
    <div>
      <SectionHeader>Appearance</SectionHeader>
      <SettingRow label="Show avatar">
        <Switch
          checked={localSettings.showAvatar}
          onCheckedChange={(v) => setLocalSettings({ showAvatar: v })}
        />
      </SettingRow>
    </div>
  )
}
