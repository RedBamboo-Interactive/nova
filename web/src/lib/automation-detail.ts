export interface AutomationParty {
  kind: string
  id?: string
  name?: string
  reason?: string
  authored?: boolean
  authoredBy?: string
  authoredAt?: string
}

export interface AutomationOwnershipDetail {
  app?: string
  plugin?: string
  actor: AutomationParty
  beneficiary: AutomationParty
}

export interface WorkflowNode {
  id: string
  type?: string
  position: { x: number; y: number }
  data: {
    label?: string
    config?: Record<string, unknown>
  }
}

export interface WorkflowEdge {
  id?: string
  source: string
  target: string
}

export interface WorkflowGraph {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
}

export interface AutomationWorkflowDetail {
  id: string
  name: string
  slug: string
  description?: string
  graph?: unknown
}

export interface AutomationDetailData {
  id: string
  prompt?: string
  ownership: AutomationOwnershipDetail
  workflow?: AutomationWorkflowDetail
}

export function expectsPrompt(actionType: string): boolean {
  return actionType === "ai-session" || actionType === "nova-session"
}

export type PromptAvailability = "not-applicable" | "loading" | "unavailable" | "missing" | "available"

export function promptAvailability(
  actionType: string,
  detail: AutomationDetailData | null,
  loading: boolean,
  loadError: boolean,
): PromptAvailability {
  if (!expectsPrompt(actionType)) return "not-applicable"
  if (loadError) return "unavailable"
  if (loading) return "loading"
  return detail?.prompt?.trim() ? "available" : "missing"
}

export function executionSummary(detail: AutomationDetailData): string {
  const { actor, beneficiary, app, plugin } = detail.ownership
  const actorName = actor.name ?? actor.id ?? displayApplication(plugin ?? app)
  if (beneficiary.kind.toLowerCase() === "user") {
    const beneficiaryName = beneficiary.name ?? beneficiary.id ?? "a user"
    return `${actorName} runs this automation for ${beneficiaryName}.`
  }
  if (beneficiary.kind.toLowerCase() === "system")
    return `${actorName} runs this automation as system work.`
  return `${actorName} has no authored beneficiary for this automation.`
}

function displayApplication(value?: string): string {
  if (!value || value === "system" || value === "redleaf") return "RedLeaf"
  if (value === "nova") return "Nova"
  return value
}

export function normalizeWorkflowGraph(value: unknown): WorkflowGraph {
  let source = value
  if (typeof source === "string") {
    try { source = JSON.parse(source) }
    catch { return { nodes: [], edges: [] } }
  }
  if (!source || typeof source !== "object") return { nodes: [], edges: [] }

  const graph = source as { nodes?: unknown; edges?: unknown }
  const nodes = Array.isArray(graph.nodes) ? graph.nodes.flatMap((candidate): WorkflowNode[] => {
    if (!candidate || typeof candidate !== "object") return []
    const node = candidate as Record<string, unknown>
    if (typeof node.id !== "string") return []
    const rawPosition = node.position && typeof node.position === "object"
      ? node.position as Record<string, unknown> : {}
    const rawData = node.data && typeof node.data === "object"
      ? node.data as Record<string, unknown> : {}
    const config = rawData.config && typeof rawData.config === "object" && !Array.isArray(rawData.config)
      ? rawData.config as Record<string, unknown> : undefined
    return [{
      id: node.id,
      type: typeof node.type === "string" ? node.type : undefined,
      position: {
        x: typeof rawPosition.x === "number" ? rawPosition.x : 0,
        y: typeof rawPosition.y === "number" ? rawPosition.y : 0,
      },
      data: {
        label: typeof rawData.label === "string" ? rawData.label : undefined,
        config,
      },
    }]
  }) : []

  const edges = Array.isArray(graph.edges) ? graph.edges.flatMap((candidate): WorkflowEdge[] => {
    if (!candidate || typeof candidate !== "object") return []
    const edge = candidate as Record<string, unknown>
    if (typeof edge.source !== "string" || typeof edge.target !== "string") return []
    return [{
      id: typeof edge.id === "string" ? edge.id : undefined,
      source: edge.source,
      target: edge.target,
    }]
  }) : []

  return { nodes, edges }
}
