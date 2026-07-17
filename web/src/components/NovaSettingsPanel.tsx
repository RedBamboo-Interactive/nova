import { SettingRow, SectionHeader } from "@redbamboo/ui"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { Toggle } from "./layout/settings-panel"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()

  return (
    <div>
      <SectionHeader>Appearance</SectionHeader>
      <SettingRow label="Show avatar">
        <Toggle
          checked={localSettings.showAvatar}
          onChange={(v) => setLocalSettings({ showAvatar: v })}
        />
      </SettingRow>
    </div>
  )
}
