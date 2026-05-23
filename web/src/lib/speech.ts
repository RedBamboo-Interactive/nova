import { createSpeechBackend, type SpeechBackend } from "@redbamboo/chat"

export function createNovaSpeechBackend(): SpeechBackend {
  return createSpeechBackend({
    transport: {
      async transcribe(audio: Blob, signal?: AbortSignal) {
        const ext = audio.type.includes("mp4") ? "mp4" : "webm"
        const form = new FormData()
        form.append("audio", audio, `recording.${ext}`)
        const res = await fetch("/api/speech/transcribe", {
          method: "POST",
          body: form,
          signal,
        })
        if (!res.ok) throw new Error("Transcription failed")
        return res.json()
      },

      async speak(text: string, voice?: string, instructions?: string, signal?: AbortSignal) {
        const res = await fetch("/api/speech/speak", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ text, voice, instructions }),
          signal,
        })
        if (!res.ok) throw new Error("Speech generation failed")
        return res.arrayBuffer()
      },

      async prompt(req, signal?: AbortSignal) {
        const res = await fetch("/api/speech/prompt", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(req),
          signal,
        })
        if (!res.ok) throw new Error("Prompt failed")
        return res.json()
      },
    },
  })
}
