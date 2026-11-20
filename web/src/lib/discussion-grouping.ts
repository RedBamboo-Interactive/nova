import type { DiscussionInfo } from "./types"

export interface DiscussionGroupLabel {
  discussionId: string
  sourceTitle: string
  group: string | null
  label: string
  icon: string
}
export interface DiscussionGroupVisual { name: string; icon: string; thumbnailDiscussionId: string | null }
export interface DiscussionGroupingView {
  groups?: DiscussionGroupVisual[] | null
  fingerprint: string
  sourceFingerprint: string | null
  assignments: DiscussionGroupLabel[]
  groupOrder: string[]
  updatedAt: string | null
  retryAfterSeconds: number
}
export interface DiscussionSection { name: string | null; icon: string; thumbnailDiscussionId?: string; items: DiscussionInfo[] }
const icons = new Set(["chats-circle", "image", "paint-brush", "film-strip", "music-notes", "book-open", "code", "cloud", "hard-drives", "feather", "mountains", "leaf", "layout", "cube"])
export const groupingIcon = (value: string | undefined) => value && icons.has(value) ? value : "chats-circle"

export function groupingInputKey(discussions: readonly DiscussionInfo[]): string {
  return JSON.stringify(discussions.filter(d => d.type === "chat" && !d.confidential && d.status !== "archived" && d.status !== "archiving")
    .map(d => [d.id, d.title, d.agentId, d.worktrees?.map(w => [w.repositoryId, w.repositoryName])]).sort((a, b) => String(a[0]).localeCompare(String(b[0]))))
}

/** Match derived labels against current facts; never decorate a confidential, changed or closed row. */
export function discussionLabel(discussion: DiscussionInfo, view: DiscussionGroupingView | null): DiscussionGroupLabel | undefined {
  if (discussion.confidential || discussion.type !== "chat" || discussion.status === "archived" || discussion.status === "archiving") return undefined
  return view?.assignments.find(a => a.discussionId === discussion.id && a.sourceTitle === discussion.title)
}

export function discussionSections(discussions: readonly DiscussionInfo[], view: DiscussionGroupingView | null): DiscussionSection[] {
  const sections = new Map<string, DiscussionSection>()
  const loose: DiscussionInfo[] = []
  for (const discussion of discussions) {
    const label = discussionLabel(discussion, view)
    if (!label?.group) { loose.push(discussion); continue }
    const visual = view?.groups?.find(g => g.name === label.group)
    const group = sections.get(label.group) ?? { name: label.group, icon: groupingIcon(visual?.icon), items: [] }
    group.items.push(discussion)
    sections.set(label.group, group)
  }
  const result: DiscussionSection[] = []
  for (const name of [...(view?.groupOrder ?? []), ...sections.keys()]) {
    const section = sections.get(name)
    if (!section) continue
    sections.delete(name)
    const representative = view?.groups?.find(g => g.name === name)?.thumbnailDiscussionId
    // Never fetch an image from a member hidden by the active Agent filter.
    if (representative && section.items.some(d => d.id === representative)) section.thumbnailDiscussionId = representative
    result.push(section)
  }
  // Loose rows keep their original recent-activity order too.
  const looseIds = new Set(loose.map(d => d.id))
  if (looseIds.size) result.push({ name: null, icon: "chats-circle", items: discussions.filter(d => looseIds.has(d.id)) })
  return result
}

export function groupingStillVisible(view: DiscussionGroupingView, discussions: readonly DiscussionInfo[]): boolean {
  return view.assignments.every(a => discussions.some(d => d.id === a.discussionId && !d.confidential
    && d.type === "chat" && d.status !== "archived" && d.status !== "archiving" && d.title === a.sourceTitle))
}
