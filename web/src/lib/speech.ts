import { createSpeechBackend, createProxySpeechTransport, type SpeechBackend } from "@redbamboo/chat"

export function createNovaSpeechBackend(): SpeechBackend {
  return createSpeechBackend({
    transport: createProxySpeechTransport(),
  })
}
