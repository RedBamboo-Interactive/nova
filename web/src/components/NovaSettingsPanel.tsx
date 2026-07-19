import { useState, useEffect } from "react"
import { SettingRow, SectionHeader, Button } from "@redbamboo/ui"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { useSettings } from "../hooks/use-settings"

function Toggle({
  checked,
  onChange,
}: {
  checked: boolean
  onChange: (v: boolean) => void
}) {
  return (
    <button
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-5 w-9 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors ${
        checked ? "bg-primary" : "bg-overlay-12"
      }`}
    >
      <span
        className={`pointer-events-none inline-block h-4 w-4 rounded-full bg-background shadow-sm transition-transform ${
          checked ? "translate-x-4" : "translate-x-0"
        }`}
      />
    </button>
  )
}

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
        <Toggle
          checked={localSettings.showAvatar}
          onChange={(v) => setLocalSettings({ showAvatar: v })}
        />
      </SettingRow>

      <SectionHeader>Docker</SectionHeader>
      <SettingRow label="Containerize AI sessions" hint="Run delegated AI sessions inside a Docker container for isolation.">
        <Toggle
          checked={dockerEnabled}
          onChange={async (v) => {
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
