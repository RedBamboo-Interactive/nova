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

import type { WorkflowNodeTypeDefinition } from "@redbamboo/workflow/graph"
export { normalizeWorkflowGraph } from "@redbamboo/workflow/graph"

export interface AutomationWorkflowDetail {
  id: string
  name: string
  slug: string
  description?: string
  graph?: unknown
  revisionId?: string
  definitionHash?: string
  versionPolicy?: string
  nodeTypes?: WorkflowNodeTypeDefinition[]
}

export interface AutomationDetailData {
  id: string
  prompt?: string
  ownership: AutomationOwnershipDetail
  workflow?: AutomationWorkflowDetail
  workflowError?: string
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
