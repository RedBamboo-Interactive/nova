import type { OutgoingMessageDraft, UploadedAttachment } from "@redbamboo/chat"
import type { VisibleAppContext } from "@redbamboo/utility"

export interface PendingVisibleContextEntry {
  context: VisibleAppContext
  screenshotAttachment?: UploadedAttachment
  discard?: () => void
}

export function applyPendingVisibleContext(
  message: OutgoingMessageDraft,
  pending: PendingVisibleContextEntry,
): OutgoingMessageDraft {
  const hasFileAttachments = !!message.attachments?.length
  const inlineScreenshot = !hasFileAttachments ? pending.context.screenshot : undefined
  if (inlineScreenshot) pending.discard?.()

  return {
    ...message,
    images: inlineScreenshot
      ? [inlineScreenshot, ...(message.images ?? [])]
      : message.images,
    attachments: hasFileAttachments && pending.screenshotAttachment
      ? [pending.screenshotAttachment, ...(message.attachments ?? [])]
      : message.attachments,
  }
}

type Listener = () => void

export class PendingVisibleContextStore {
  private readonly entries = new Map<string, PendingVisibleContextEntry>()
  private readonly listeners = new Set<Listener>()
  private revision = 0

  get(discussionId: string | null | undefined): PendingVisibleContextEntry | null {
    return discussionId ? this.entries.get(discussionId) ?? null : null
  }

  set(discussionId: string, entry: PendingVisibleContextEntry): void {
    const previous = this.entries.get(discussionId)
    this.entries.set(discussionId, entry)
    previous?.discard?.()
    this.emit()
  }

  consume(discussionId: string): PendingVisibleContextEntry | null {
    const entry = this.entries.get(discussionId) ?? null
    if (!entry) return null
    this.entries.delete(discussionId)
    this.emit()
    return entry
  }

  clear(discussionId: string | null | undefined): void {
    if (!discussionId) return
    const entry = this.entries.get(discussionId)
    if (!entry) return
    this.entries.delete(discussionId)
    entry.discard?.()
    this.emit()
  }

  dispose(): void {
    if (this.entries.size === 0) return
    for (const entry of this.entries.values()) entry.discard?.()
    this.entries.clear()
    this.emit()
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  getSnapshot(): number {
    return this.revision
  }

  private emit(): void {
    this.revision += 1
    for (const listener of this.listeners) listener()
  }
}
