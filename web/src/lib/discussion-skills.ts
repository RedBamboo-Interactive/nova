// Pure helpers for Skills and MCP servers attached to one discussion on demand (no runtime imports, so node tests load it).
import type { PermissionApproval } from "@redbamboo/chat"

/** What a discussion attaches on demand: a Skill, or an MCP server whose tools RedLeaf's gateway serves. */
export type CapabilityKind = "skill" | "mcp"

/** A Skill (or MCP server) attached to this discussion only, beside the Agent's defaults. */
export interface SkillAttachment {
  kind: CapabilityKind
  entityId: string
  slug: string
  name: string
  /** Who asked for it: the person, or the Agent (itself, or by a request the person approved). */
  attachedBy: "user" | "agent"
  attachedById?: string | null
  attachedByName?: string | null
  attachedAt: string
  /** Set when the Agent asked and the person approved. */
  approvedById?: string | null
}

/** The Agent's request to attach a Skill (or MCP server) its owner did not make attachable. */
export interface SkillRequest {
  id: string
  kind: CapabilityKind
  entityId: string
  slug: string
  name: string
  reason?: string | null
  requestedById: string
  requestedByName?: string | null
  requestedAt: string
  status: "pending" | "approved" | "declined"
  decidedAt?: string | null
}

export interface SkillSummary {
  entityId: string
  slug: string
  name: string
  description: string
}

/** GET /discussions/{id}/skills?library=true */
export interface DiscussionSkillsResponse {
  items: SkillAttachment[]
  requests: SkillRequest[]
  defaults: SkillSummary[]
  optional: SkillSummary[]
  library: SkillSummary[] | null
}

export function skillsPath(discussionId: string): string {
  return `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/skills`
}

export function mcpServersPath(discussionId: string): string {
  return `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/mcp-servers`
}

/** The attach route of one kind: /skills or /mcp-servers. */
export function capabilityPath(discussionId: string, kind: CapabilityKind): string {
  return kind === "mcp" ? mcpServersPath(discussionId) : skillsPath(discussionId)
}

/** Decides any request, a Skill's or an MCP server's. */
export function skillRequestPath(discussionId: string, requestId: string): string {
  return `/api/apps/nova/discussions/${encodeURIComponent(discussionId)}/capability-requests/${encodeURIComponent(requestId)}`
}

/** What the person calls each kind. */
export function capabilityNoun(kind: CapabilityKind): string {
  return kind === "mcp" ? "MCP server" : "Skill"
}

export function capabilityIcon(kind: CapabilityKind): string {
  return kind === "mcp" ? "ph-bold ph-plugs-connected" : "ph-bold ph-puzzle-piece"
}

export function readSkillAttachments(value: unknown): SkillAttachment[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is SkillAttachment =>
    !!item && typeof item === "object"
    && typeof (item as SkillAttachment).entityId === "string"
    && typeof (item as SkillAttachment).slug === "string")
}

export function readSkillRequests(value: unknown): SkillRequest[] {
  if (!Array.isArray(value)) return []
  return value.filter((item): item is SkillRequest =>
    !!item && typeof item === "object"
    && typeof (item as SkillRequest).id === "string"
    && typeof (item as SkillRequest).entityId === "string")
}

/** "you" for the person, else the Agent's name: who put the Skill on this discussion. */
export function skillOriginLabel(attachment: Pick<SkillAttachment, "attachedBy">, agentName: string | null | undefined): string {
  return attachment.attachedBy === "user" ? "you" : (agentName?.trim() || "agent")
}

export function skillChipTitle(attachment: SkillAttachment, agentName: string | null | undefined): string {
  const by = attachment.attachedBy === "user"
    ? "You attached it"
    : attachment.approvedById ? `${agentName?.trim() || "The agent"} asked, you approved` : `${agentName?.trim() || "The agent"} attached it`
  const tools = attachment.kind === "mcp" ? " Its tools answer here through Leaf's MCP gateway." : ""
  return `${attachment.name} (${attachment.slug})\n${by}. This discussion only.${tools}`
}

/**
 * The waiting request as the shared approval card's model: one Skill, attach or don't. Settled
 * requests are not drawn; the chip (approved) or the agent's note (declined) already says how it ended.
 */
export function skillRequestApproval(request: SkillRequest): PermissionApproval {
  return {
    key: `${request.kind === "mcp" ? "mcp" : "skill"}-request:${request.id}`,
    requestId: request.id,
    request: {
      toolName: `Attach ${capabilityNoun(request.kind ?? "skill")} · ${request.name}`,
      summary: request.reason?.trim() || null,
      options: ["allow_once", "deny"],
    },
    outcome: null,
  }
}

export function pendingSkillRequests(requests: readonly SkillRequest[] | undefined): SkillRequest[] {
  return (requests ?? []).filter(request => request.status === "pending")
}

/** What the picker says under a Skill's name: where it already stands for this discussion. */
export function skillAvailability(
  skill: Pick<SkillSummary, "entityId">,
  view: Pick<DiscussionSkillsResponse, "items" | "defaults" | "optional">,
): "attached" | "default" | "optional" | null {
  if (view.items.some(item => item.entityId === skill.entityId)) return "attached"
  if (view.defaults.some(item => item.entityId === skill.entityId)) return "default"
  if (view.optional.some(item => item.entityId === skill.entityId)) return "optional"
  return null
}

export function skillAvailabilityLabel(availability: ReturnType<typeof skillAvailability>, agentName: string | null | undefined): string | undefined {
  const agent = agentName?.trim() || "The agent"
  switch (availability) {
    case "attached": return "Attached here"
    case "default": return `${agent} always has it`
    case "optional": return `${agent} may attach it`
    default: return undefined
  }
}

/** Free-text filter over name, slug and description, for the picker's search box. */
export function filterSkills(skills: readonly SkillSummary[], query: string): SkillSummary[] {
  const needle = query.trim().toLowerCase()
  if (!needle) return [...skills]
  return skills.filter(skill =>
    skill.name.toLowerCase().includes(needle)
    || skill.slug.toLowerCase().includes(needle)
    || skill.description.toLowerCase().includes(needle))
}
