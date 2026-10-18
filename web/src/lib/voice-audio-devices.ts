export interface VoiceAudioSettings {
  voiceInputDeviceId: string | null
  voiceInputDeviceLabel: string | null
  voiceOutputDeviceId: string | null
  voiceOutputDeviceLabel: string | null
  voiceProcessing: boolean
}

interface AudioSinkTarget {
  setSinkId?: (deviceId: string) => Promise<void>
}

export function buildVoiceAudioConstraints(
  settings: Pick<VoiceAudioSettings, "voiceInputDeviceId" | "voiceProcessing">,
): MediaTrackConstraints {
  const constraints: MediaTrackConstraints = {
    echoCancellation: settings.voiceProcessing,
    noiseSuppression: settings.voiceProcessing,
    autoGainControl: settings.voiceProcessing,
  }
  if (settings.voiceInputDeviceId) {
    constraints.deviceId = { exact: settings.voiceInputDeviceId }
  }
  return constraints
}

export function isMissingVoiceInputDeviceError(error: unknown): boolean {
  if (!error || typeof error !== "object" || !("name" in error)) return false
  const name = String(error.name)
  return name === "OverconstrainedError" || name === "NotFoundError"
}

export async function acquireVoiceInput<T>(
  settings: Pick<VoiceAudioSettings, "voiceInputDeviceId" | "voiceInputDeviceLabel" | "voiceProcessing">,
  request: (constraints: MediaTrackConstraints) => Promise<T>,
): Promise<{ stream: T; notice: string | null }> {
  try {
    return {
      stream: await request(buildVoiceAudioConstraints(settings)),
      notice: null,
    }
  } catch (error) {
    if (!settings.voiceInputDeviceId || !isMissingVoiceInputDeviceError(error)) throw error
    const stream = await request(buildVoiceAudioConstraints({
      voiceInputDeviceId: null,
      voiceProcessing: settings.voiceProcessing,
    }))
    const deviceName = settings.voiceInputDeviceLabel || "The selected microphone"
    return {
      stream,
      notice: `${deviceName} is unavailable. Using the system-default microphone.`,
    }
  }
}

export function supportsVoiceOutputSelection(): boolean {
  return typeof HTMLMediaElement !== "undefined"
    && typeof (HTMLMediaElement.prototype as AudioSinkTarget).setSinkId === "function"
}

export async function applyVoiceOutputDevice(
  target: AudioSinkTarget,
  settings: Pick<VoiceAudioSettings, "voiceOutputDeviceId" | "voiceOutputDeviceLabel">,
): Promise<string | null> {
  if (!settings.voiceOutputDeviceId || typeof target.setSinkId !== "function") return null
  try {
    await target.setSinkId(settings.voiceOutputDeviceId)
    return null
  } catch {
    try {
      await target.setSinkId("")
    } catch { /* playback will still use the browser's current/default route */ }
    const deviceName = settings.voiceOutputDeviceLabel || "The selected speaker"
    return `${deviceName} is unavailable. Using the system-default speaker.`
  }
}
