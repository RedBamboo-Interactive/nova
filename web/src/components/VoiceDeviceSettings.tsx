import { useCallback, useEffect, useState } from "react"
import { Button, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, SettingRow, Switch } from "@redbamboo/ui"
import { useLocalSettings } from "../hooks/use-local-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"
import { supportsVoiceOutputSelection } from "../lib/voice-audio-devices"

const SYSTEM_DEFAULT_DEVICE = "__nova_system_default__"

function listedDevices(devices: MediaDeviceInfo[], kind: MediaDeviceKind): MediaDeviceInfo[] {
  const seen = new Set<string>()
  return devices.filter(device => {
    if (device.kind !== kind || !device.deviceId || device.deviceId === "default" || seen.has(device.deviceId)) return false
    seen.add(device.deviceId)
    return true
  })
}

function deviceLabel(device: MediaDeviceInfo, index: number, fallback: string): string {
  return device.label || `${fallback} ${index + 1}`
}

export function VoiceDeviceSettings() {
  const settings = useLocalSettings()
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([])
  const [deviceError, setDeviceError] = useState<string | null>(null)
  const [requestingAccess, setRequestingAccess] = useState(false)
  const mediaDevices = typeof navigator === "undefined" ? undefined : navigator.mediaDevices
  const outputSelectionSupported = supportsVoiceOutputSelection()

  const refreshDevices = useCallback(async () => {
    if (!mediaDevices?.enumerateDevices) {
      setDeviceError("This browser cannot list audio devices.")
      return
    }
    try {
      setDevices(await mediaDevices.enumerateDevices())
      setDeviceError(null)
    } catch {
      setDeviceError("Audio devices could not be listed.")
    }
  }, [mediaDevices])

  useEffect(() => {
    void refreshDevices()
    if (!mediaDevices?.addEventListener) return
    const onDeviceChange = () => { void refreshDevices() }
    mediaDevices.addEventListener("devicechange", onDeviceChange)
    return () => mediaDevices.removeEventListener("devicechange", onDeviceChange)
  }, [refreshDevices])

  const grantMicrophoneAccess = async () => {
    if (!mediaDevices?.getUserMedia) return
    setRequestingAccess(true)
    try {
      const stream = await mediaDevices.getUserMedia({ audio: true })
      stream.getTracks().forEach(track => track.stop())
      await refreshDevices()
    } catch {
      setDeviceError("Microphone access was not granted, so device names remain hidden.")
    } finally {
      setRequestingAccess(false)
    }
  }

  const inputDevices = listedDevices(devices, "audioinput")
  const outputDevices = listedDevices(devices, "audiooutput")
  const audioDevices = devices.filter(device => device.kind === "audioinput" || device.kind === "audiooutput")
  const labelsHidden = audioDevices.some(device => !device.label)
  const selectedInput = inputDevices.find(device => device.deviceId === settings.voiceInputDeviceId)
  const selectedOutput = outputDevices.find(device => device.deviceId === settings.voiceOutputDeviceId)

  useEffect(() => {
    const labels: { voiceInputDeviceLabel?: string; voiceOutputDeviceLabel?: string } = {}
    if (selectedInput?.label && selectedInput.label !== settings.voiceInputDeviceLabel) {
      labels.voiceInputDeviceLabel = selectedInput.label
    }
    if (selectedOutput?.label && selectedOutput.label !== settings.voiceOutputDeviceLabel) {
      labels.voiceOutputDeviceLabel = selectedOutput.label
    }
    if (Object.keys(labels).length > 0) setLocalSettings(labels)
  }, [selectedInput?.label, selectedOutput?.label, settings.voiceInputDeviceLabel, settings.voiceOutputDeviceLabel])

  const inputValue = settings.voiceInputDeviceId || SYSTEM_DEFAULT_DEVICE
  const outputValue = settings.voiceOutputDeviceId || SYSTEM_DEFAULT_DEVICE
  const inputDisplayName = settings.voiceInputDeviceId
    ? selectedInput
      ? deviceLabel(selectedInput, inputDevices.indexOf(selectedInput), "Microphone")
      : settings.voiceInputDeviceLabel || "Unavailable microphone"
    : "System default"
  const outputDisplayName = settings.voiceOutputDeviceId
    ? selectedOutput
      ? deviceLabel(selectedOutput, outputDevices.indexOf(selectedOutput), "Speaker")
      : settings.voiceOutputDeviceLabel || "Unavailable speaker"
    : "System default"
  const microphoneHint = deviceError
    ? `Choose the input for the next Natural Voice session. ${deviceError}`
    : "Choose the input for the next Natural Voice session."
  const speakerHint = outputSelectionSupported
    ? "Choose where Nova plays the next Natural Voice session."
    : "This browser does not support choosing a speaker; audio uses the system default."

  return (
    <>
      <SettingRow label="Microphone" hint={microphoneHint}>
        <div className="flex items-center gap-2">
          {labelsHidden && (
            <Button
              type="button"
              variant="outline"
              size="xs"
              disabled={requestingAccess}
              onClick={() => { void grantMicrophoneAccess() }}
            >
              {requestingAccess ? "Requesting…" : "Allow access"}
            </Button>
          )}
          <Select
            value={inputValue}
            disabled={!mediaDevices?.enumerateDevices}
            onValueChange={(value: unknown) => {
              const deviceId = typeof value === "string" && value !== SYSTEM_DEFAULT_DEVICE ? value : null
              const device = inputDevices.find(candidate => candidate.deviceId === deviceId)
              setLocalSettings({
                voiceInputDeviceId: deviceId,
                voiceInputDeviceLabel: device?.label || null,
              })
            }}
          >
            <SelectTrigger className="w-56 max-w-[min(14rem,45vw)]" aria-label="Microphone">
              <SelectValue>{inputDisplayName}</SelectValue>
            </SelectTrigger>
            <SelectContent align="end">
              <SelectItem value={SYSTEM_DEFAULT_DEVICE}>System default</SelectItem>
              {settings.voiceInputDeviceId && !selectedInput && (
                <SelectItem value={settings.voiceInputDeviceId}>
                  {settings.voiceInputDeviceLabel || "Saved microphone"} (unavailable)
                </SelectItem>
              )}
              {inputDevices.map((device, index) => (
                <SelectItem key={device.deviceId} value={device.deviceId}>
                  {deviceLabel(device, index, "Microphone")}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </SettingRow>
      <SettingRow label="Speaker" hint={speakerHint}>
        <Select
          value={outputValue}
          disabled={!outputSelectionSupported || !mediaDevices?.enumerateDevices}
          onValueChange={(value: unknown) => {
            const deviceId = typeof value === "string" && value !== SYSTEM_DEFAULT_DEVICE ? value : null
            const device = outputDevices.find(candidate => candidate.deviceId === deviceId)
            setLocalSettings({
              voiceOutputDeviceId: deviceId,
              voiceOutputDeviceLabel: device?.label || null,
            })
          }}
        >
          <SelectTrigger className="w-56 max-w-[min(14rem,45vw)]" aria-label="Speaker">
            <SelectValue>{outputDisplayName}</SelectValue>
          </SelectTrigger>
          <SelectContent align="end">
            <SelectItem value={SYSTEM_DEFAULT_DEVICE}>System default</SelectItem>
            {settings.voiceOutputDeviceId && !selectedOutput && (
              <SelectItem value={settings.voiceOutputDeviceId}>
                {settings.voiceOutputDeviceLabel || "Saved speaker"} (unavailable)
              </SelectItem>
            )}
            {outputDevices.map((device, index) => (
              <SelectItem key={device.deviceId} value={device.deviceId}>
                {deviceLabel(device, index, "Speaker")}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </SettingRow>
      <SettingRow
        label="Voice processing"
        hint="Echo cancellation, noise suppression, and auto gain apply on the next session. Turn this off for virtual cables or line inputs."
      >
        <Switch
          checked={settings.voiceProcessing}
          onCheckedChange={voiceProcessing => setLocalSettings({ voiceProcessing })}
          aria-label="Voice processing"
        />
      </SettingRow>
    </>
  )
}
