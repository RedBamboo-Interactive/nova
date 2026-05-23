import { useState, useEffect } from "react"
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  Button,
  Input,
} from "@redbamboo/ui"
import { TunnelSettingsPanel } from "@redbamboo/utility"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { setSettings as setLocalSettings } from "@/lib/settings-store"
import { useSettings } from "@/hooks/use-settings"

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
}

function SettingRow({
  label,
  hint,
  children,
}: {
  label: string
  hint?: string
  children: React.ReactNode
}) {
  return (
    <div className="py-2.5">
      <div className="flex items-center justify-between gap-4">
        <span className="text-sm text-text-muted">{label}</span>
        {children}
      </div>
      {hint && (
        <p className="text-xs text-muted-a60 mt-1 leading-relaxed">{hint}</p>
      )}
    </div>
  )
}

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

function SectionHeader({ children }: { children: React.ReactNode }) {
  return (
    <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3">
      {children}
    </div>
  )
}

export function SettingsModal({ open, onOpenChange }: Props) {
  const localSettings = useLocalSettings()
  const { settings, saving, updateIdentity, updateGeneral } = useSettings()
  const [identityDraft, setIdentityDraft] = useState("")
  const [identityDirty, setIdentityDirty] = useState(false)

  useEffect(() => {
    if (settings?.identity && !identityDirty) {
      setIdentityDraft(settings.identity)
    }
  }, [settings?.identity, identityDirty])

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md sm:max-w-lg max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle className="text-lg">Settings</DialogTitle>
        </DialogHeader>

        <div className="divide-y divide-overlay-6 -mx-1">
          {/* Appearance */}
          <div className="pb-4 px-1">
            <SectionHeader>Appearance</SectionHeader>
            <SettingRow label="Light mode">
              <Toggle
                checked={localSettings.theme === "light"}
                onChange={(v) =>
                  setLocalSettings({ theme: v ? "light" : "dark" })
                }
              />
            </SettingRow>
            <SettingRow label="High contrast">
              <Toggle
                checked={localSettings.contrast === "high"}
                onChange={(v) =>
                  setLocalSettings({ contrast: v ? "high" : "low" })
                }
              />
            </SettingRow>
          </div>

          {/* Identity */}
          <div className="py-4 px-1">
            <SectionHeader>Identity</SectionHeader>
            <p className="text-xs text-muted-a60 mb-3 leading-relaxed">
              Nova's personality, tone, and behavioral guidelines.
            </p>
            <textarea
              className="w-full h-56 bg-overlay-6 border border-overlay-10 rounded px-3 py-2 text-xs font-mono text-contrast outline-none focus:border-overlay-20 resize-y"
              value={identityDraft}
              onChange={(e) => {
                setIdentityDraft(e.target.value)
                setIdentityDirty(true)
              }}
            />
            {identityDirty && (
              <div className="flex items-center gap-2 mt-2">
                <Button
                  size="sm"
                  disabled={saving}
                  onClick={async () => {
                    await updateIdentity(identityDraft)
                    setIdentityDirty(false)
                  }}
                >
                  {saving ? "Saving..." : "Save"}
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    if (settings) setIdentityDraft(settings.identity)
                    setIdentityDirty(false)
                  }}
                >
                  Discard
                </Button>
              </div>
            )}
          </div>

          {/* General */}
          <div className="py-4 px-1">
            <SectionHeader>General</SectionHeader>
            <SettingRow label="RedCompute URL">
              <Input
                className="w-48 h-7 text-xs"
                value={settings?.general.redComputeUrl ?? ""}
                onChange={(e) =>
                  updateGeneral({ redComputeUrl: e.target.value })
                }
              />
            </SettingRow>
            <SettingRow
              label="Claude timeout"
              hint="Maximum seconds to wait for a response"
            >
              <input
                type="number"
                className="bg-overlay-6 border border-overlay-10 rounded px-2 py-0.5 text-xs text-contrast outline-none focus:border-overlay-20 w-16 text-right"
                value={settings?.general.claudeTimeoutSeconds ?? 180}
                onChange={(e) =>
                  updateGeneral({
                    claudeTimeoutSeconds: parseInt(e.target.value) || 180,
                  })
                }
              />
            </SettingRow>
            <SettingRow
              label="Max concurrency"
              hint="Parallel Claude invocations"
            >
              <input
                type="number"
                className="bg-overlay-6 border border-overlay-10 rounded px-2 py-0.5 text-xs text-contrast outline-none focus:border-overlay-20 w-16 text-right"
                min={1}
                max={4}
                value={settings?.general.maxConcurrentInvocations ?? 1}
                onChange={(e) =>
                  updateGeneral({
                    maxConcurrentInvocations: parseInt(e.target.value) || 1,
                  })
                }
              />
            </SettingRow>
          </div>

          {/* Remote Access */}
          <div className="pt-4 px-1">
            <SectionHeader>Remote Access</SectionHeader>
            <TunnelSettingsPanel />
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
