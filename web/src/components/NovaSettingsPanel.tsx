import { useState, useEffect } from "react"
import { SettingRow, SectionHeader, Button, Switch } from "@redbamboo/ui"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { useSettings } from "../hooks/use-settings"

export function NovaSettingsPanel() {
  const localSettings = useLocalSettings()
  const { settings, saving, updateDocker } = useSettings()
  const [dockerEnabled, setDockerEnabled] = useState(false)
  const [dockerImage, setDockerImage] = useState("")
  const [dockerDirty, setDockerDirty] = useState(false)

  useEffect(() => {
    if (settings?.docker && !dockerDirty) {
      setDockerEnabled(settings.docker.enabled)
      setDockerImage(settings.docker.image ?? "")
    }
  }, [settings?.docker, dockerDirty])

  return (
    <div>
      <SectionHeader>Appearance</SectionHeader>
      <SettingRow label="Show avatar">
        <Switch
          checked={localSettings.showAvatar}
          onCheckedChange={(v) => setLocalSettings({ showAvatar: v })}
        />
      </SettingRow>

      <SectionHeader>Docker</SectionHeader>
      <SettingRow label="Containerize AI sessions" hint="Run delegated AI sessions inside a Docker container for isolation.">
        <Switch
          checked={dockerEnabled}
          onCheckedChange={async (v) => {
            setDockerEnabled(v)
            if (!v) {
              setDockerImage("")
              setDockerDirty(false)
              await updateDocker(null)
            } else {
              setDockerDirty(true)
            }
          }}
        />
      </SettingRow>
      {dockerEnabled && (
        <>
          <div className="mt-2">
            <input
              type="text"
              placeholder="redsuite/ai-sandbox:latest"
              className="w-full bg-overlay-6 border border-overlay-10 rounded px-3 py-1.5 text-sm text-contrast outline-none focus:border-overlay-20 font-mono"
              value={dockerImage}
              onChange={(e) => {
                setDockerImage(e.target.value)
                setDockerDirty(true)
              }}
            />
          </div>
          {dockerDirty && dockerImage.trim() && (
            <div className="flex items-center gap-2 mt-2">
              <Button
                size="sm"
                disabled={saving}
                onClick={async () => {
                  await updateDocker(dockerImage.trim())
                  setDockerDirty(false)
                }}
              >
                {saving ? "Saving..." : "Save"}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => {
                  setDockerImage(settings?.docker?.image ?? "")
                  setDockerEnabled(settings?.docker?.enabled ?? false)
                  setDockerDirty(false)
                }}
              >
                Discard
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
