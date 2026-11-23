import type { DiscussionInfo } from "./types"

export interface DiscussionGroupLabel {
  discussionId: string
  sourceTitle: string
  group: string | null
  label: string
  icon: string
  thumbnailTarget?: string | null
}
export interface DiscussionGroupVisual { name: string; icon: string; thumbnailDiscussionId: string | null; thumbnailTarget?: string | null }
export interface DiscussionGroupingView {
  groups?: DiscussionGroupVisual[] | null
  fingerprint: string
  sourceFingerprint: string | null
  assignments: DiscussionGroupLabel[]
  groupOrder: string[]
  updatedAt: string | null
  retryAfterSeconds: number
}
export interface DiscussionSection { name: string | null; icon: string; thumbnailDiscussionId?: string; thumbnailTarget?: string; items: DiscussionInfo[] }
type NamedDiscussionSection = DiscussionSection & { name: string }

export const discussionGroupSortOptions = [
  { value: "activity", label: "Most recent activity" },
  { value: "manual", label: "Manual" },
  { value: "alphabetical", label: "Alphabetical" },
  { value: "size", label: "Most discussions" },
] as const

export type DiscussionGroupSortMode = typeof discussionGroupSortOptions[number]["value"]
export const groupedDiscussionSortOptions = [
  { value: "activity", label: "Most recent activity" },
  { value: "alphabetical", label: "Alphabetical" },
  { value: "newest", label: "Newest created" },
  { value: "oldest", label: "Oldest created" },
] as const

export type GroupedDiscussionSortMode = typeof groupedDiscussionSortOptions[number]["value"]

export interface DiscussionGroupingSort {
  mode: DiscussionGroupSortMode
  manualOrder: readonly string[]
  discussionMode?: GroupedDiscussionSortMode
  refineTitles?: boolean
}

const titleCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" })
const icons = new Set(["chats-circle", "image", "paint-brush", "film-strip", "music-notes", "book-open", "code", "cloud", "hard-drives", "feather", "mountains", "leaf", "layout", "cube"])
export const groupingIcon = (value: string | undefined) => value && icons.has(value) ? value : "chats-circle"

// Keep the early limit and later interval in sync with DiscussionGroupingPolicy.
function contextRevision(messageCount: number): number {
  const count = Math.max(0, messageCount)
  if (count <= 12) return count
  return 12 + Math.floor((count - 12) / 8) * 8
}

export function groupingInputKey(discussions: readonly DiscussionInfo[]): string {
  return JSON.stringify(discussions.filter(d => d.type === "chat" && !d.confidential && d.status !== "archived" && d.status !== "archiving")
    .map(d => [d.id, d.title, d.agentId, d.sessionId, contextRevision(d.messageCount), d.worktrees?.map(w => [w.repositoryId, w.repositoryName])]).sort((a, b) => String(a[0]).localeCompare(String(b[0]))))
}

/** Match derived labels against current facts; never decorate a confidential, changed or closed row. */
export function discussionLabel(discussion: DiscussionInfo, view: DiscussionGroupingView | null): DiscussionGroupLabel | undefined {
  if (discussion.confidential || discussion.type !== "chat" || discussion.status === "archived" || discussion.status === "archiving") return undefined
  return view?.assignments.find(a => a.discussionId === discussion.id && a.sourceTitle === discussion.title)
}

export function sidebarDiscussionTitle(discussion: DiscussionInfo, refinedTitle?: string): string {
  return refinedTitle || discussion.title?.trim() || "New discussion"
}

function sortDiscussionRows(
  rows: DiscussionInfo[],
  view: DiscussionGroupingView | null,
  mode: GroupedDiscussionSortMode,
  refineTitles: boolean,
) {
  if (mode === "alphabetical") {
    const titles = new Map(rows.map(row => [
      row, sidebarDiscussionTitle(row, refineTitles ? discussionLabel(row, view)?.label : undefined),
    ]))
    rows.sort((a, b) => titleCollator.compare(titles.get(a)!, titles.get(b)!))
    return
  }

  const dates = new Map(rows.map(row => [row, Date.parse(mode === "activity" ? row.lastActivity : row.createdAt) || 0]))
  const direction = mode === "oldest" ? 1 : -1
  rows.sort((a, b) => direction * (dates.get(a)! - dates.get(b)!))
}

export function discussionSections(
  discussions: readonly DiscussionInfo[],
  view: DiscussionGroupingView | null,
  sort?: DiscussionGroupingSort,
): DiscussionSection[] {
  const sections = new Map<string, NamedDiscussionSection>()
  const loose: DiscussionInfo[] = []
  for (const discussion of discussions) {
    const label = discussionLabel(discussion, view)
    if (!label?.group) { loose.push(discussion); continue }
    const visual = view?.groups?.find(g => g.name === label.group)
    const group = sections.get(label.group) ?? { name: label.group, icon: groupingIcon(visual?.icon), items: [] }
    group.items.push(discussion)
    sections.set(label.group, group)
  }
  const result: NamedDiscussionSection[] = []
  for (const name of [...(view?.groupOrder ?? []), ...sections.keys()]) {
    const section = sections.get(name)
    if (!section) continue
    sections.delete(name)
    const visual = view?.groups?.find(g => g.name === name)
    const representative = visual?.thumbnailDiscussionId
    // Never fetch an image from a member hidden by the active Agent filter.
    if (representative && section.items.some(d => d.id === representative)) {
      section.thumbnailDiscussionId = representative
      section.thumbnailTarget = visual?.thumbnailTarget ?? undefined
    }
    result.push(section)
  }
  const mode = sort?.mode ?? "activity"
  if (mode === "manual") {
    const ranks = new Map(sort?.manualOrder.map((name, index) => [name, index]))
    const rank = (section: NamedDiscussionSection) => ranks.get(section.name) ?? ranks.size
    result.sort((a, b) => rank(a) - rank(b))
  } else if (mode === "alphabetical") {
    result.sort((a, b) => titleCollator.compare(a.name, b.name))
  } else {
    const latestActivity = (section: DiscussionSection) => section.items.reduce(
      (latest, row) => Math.max(latest, Date.parse(row.lastActivity) || 0), 0,
    )
    const activity = new Map(result.map(section => [section, latestActivity(section)]))
    result.sort((a, b) => {
      const sizeOrder = mode === "size" ? b.items.length - a.items.length : 0
      return sizeOrder || activity.get(b)! - activity.get(a)!
    })
  }
  const discussionMode = sort?.discussionMode
  if (discussionMode && result.length) {
    for (const section of result) sortDiscussionRows(section.items, view, discussionMode, sort.refineTitles ?? true)
    sortDiscussionRows(loose, view, discussionMode, sort.refineTitles ?? true)
  }
  return loose.length ? [...result, { name: null, icon: "chats-circle", items: loose }] : result
}

/** Move among visible projects while leaving hidden projects in their saved slots. */
export function moveDiscussionGroup(
  order: readonly string[],
  visibleNames: readonly string[],
  source: string,
  target: string,
): string[] | null {
  const visible = new Set(visibleNames)
  const reordered = order.filter(name => visible.has(name))
  const from = reordered.indexOf(source)
  const to = reordered.indexOf(target)
  if (from < 0 || to < 0 || from === to) return null
  reordered.splice(from, 1)
  reordered.splice(to, 0, source)
  let index = 0
  return order.map(name => visible.has(name) ? reordered[index++] : name)
}

export function groupingStillVisible(view: DiscussionGroupingView, discussions: readonly DiscussionInfo[],
  isArchivePending: (id: string) => boolean = () => false): boolean {
  const byId = new Map(discussions.map(discussion => [discussion.id, discussion]))
  return view.assignments.every(assignment => {
    const discussion = byId.get(assignment.discussionId)
    if (!discussion) return isArchivePending(assignment.discussionId)
    return !discussion.confidential && discussion.type === "chat"
  })
}
