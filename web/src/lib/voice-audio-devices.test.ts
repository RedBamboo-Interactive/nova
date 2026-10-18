import test from "node:test"
import assert from "node:assert/strict"
import {
  acquireVoiceInput,
  applyVoiceOutputDevice,
  buildVoiceAudioConstraints,
  isMissingVoiceInputDeviceError,
} from "./voice-audio-devices.ts"

test("voice audio constraints preserve today's processed system-default behavior", () => {
  assert.deepEqual(buildVoiceAudioConstraints({
    voiceInputDeviceId: null,
    voiceProcessing: true,
  }), {
    echoCancellation: true,
    noiseSuppression: true,
    autoGainControl: true,
  })
})

test("voice audio constraints select an exact input and disable all processing together", () => {
  assert.deepEqual(buildVoiceAudioConstraints({
    voiceInputDeviceId: "voicemeeter-b1",
    voiceProcessing: false,
  }), {
    echoCancellation: false,
    noiseSuppression: false,
    autoGainControl: false,
    deviceId: { exact: "voicemeeter-b1" },
  })
})

test("missing selected input falls back once to the system default and returns a notice", async () => {
  const requested: MediaTrackConstraints[] = []
  const result = await acquireVoiceInput({
    voiceInputDeviceId: "missing-device",
    voiceInputDeviceLabel: "VoiceMeeter Out B1",
    voiceProcessing: false,
  }, async constraints => {
    requested.push(constraints)
    if (requested.length === 1) throw { name: "OverconstrainedError" }
    return "fallback-stream"
  })

  assert.equal(result.stream, "fallback-stream")
  assert.equal(result.notice, "VoiceMeeter Out B1 is unavailable. Using the system-default microphone.")
  assert.deepEqual(requested[0]?.deviceId, { exact: "missing-device" })
  assert.equal(requested[1]?.deviceId, undefined)
  assert.equal(requested[1]?.echoCancellation, false)
})

test("input fallback is limited to missing-device failures", async () => {
  assert.equal(isMissingVoiceInputDeviceError({ name: "NotFoundError" }), true)
  assert.equal(isMissingVoiceInputDeviceError({ name: "NotAllowedError" }), false)
  await assert.rejects(
    acquireVoiceInput({
      voiceInputDeviceId: "private-device",
      voiceInputDeviceLabel: "Private microphone",
      voiceProcessing: true,
    }, async () => { throw { name: "NotAllowedError" } }),
    error => (error as { name?: string }).name === "NotAllowedError",
  )
})

test("failed output selection restores the default sink and returns a notice", async () => {
  const sinks: string[] = []
  const notice = await applyVoiceOutputDevice({
    async setSinkId(deviceId) {
      sinks.push(deviceId)
      if (deviceId) throw new Error("device disappeared")
    },
  }, {
    voiceOutputDeviceId: "missing-output",
    voiceOutputDeviceLabel: "VoiceMeeter AUX Input",
  })

  assert.deepEqual(sinks, ["missing-output", ""])
  assert.equal(notice, "VoiceMeeter AUX Input is unavailable. Using the system-default speaker.")
})

test("system-default output does not override the audio sink", async () => {
  let calls = 0
  const notice = await applyVoiceOutputDevice({
    async setSinkId() { calls++ },
  }, {
    voiceOutputDeviceId: null,
    voiceOutputDeviceLabel: null,
  })
  assert.equal(calls, 0)
  assert.equal(notice, null)
})
