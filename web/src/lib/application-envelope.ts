const ENVELOPE_PREFIX = "Application conversation envelope JSON"
const START_TAG = "<external-input-json>"
const END_TAG = "</external-input-json>"

/**
 * Apps that own an Agent discussion (Storyboard) send each participant turn to the
 * model wrapped in an application envelope, and the transcript stores that envelope.
 * Returns only what the participant wrote, or the content unchanged when it is not
 * exactly such an envelope. Mirrors Nova's server-side UnwrapApplicationEnvelope.
 */
export function unwrapApplicationEnvelope(content: string): string {
  if (!content.startsWith(ENVELOPE_PREFIX)) return content
  const start = content.indexOf(START_TAG)
  const end = content.indexOf(END_TAG)
  if (start < 0 || end <= start + START_TAG.length) return content
  try {
    const envelope: unknown = JSON.parse(content.slice(start + START_TAG.length, end))
    if (!envelope || typeof envelope !== "object") return content
    const { requestId, applicationId, message } = envelope as Record<string, unknown>
    if (typeof requestId !== "string" || typeof applicationId !== "string" || typeof message !== "string")
      return content
    return message
  } catch {
    return content
  }
}
