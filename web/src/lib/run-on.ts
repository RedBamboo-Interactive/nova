/** GET /compute/nodes as RunOnChoice reads it: the nodes, and the id older records name node zero by. */
export interface NodeListBody {
  nodes?: Array<{ id?: unknown; name?: unknown; isLocal?: unknown }>
  legacyNodeId?: unknown
}

/**
 * The name of the node a session runs on, for "Running on <node>": its own entry, or node zero when the session
 * names the legacy node (sessions recorded before node ids). Null when the session names no node or one the
 * list does not show (a node this person may not see), so nothing is guessed.
 */
export function runOnSessionNode(list: NodeListBody, sessionNodeId: string | null | undefined): string | null {
  if (!sessionNodeId || !Array.isArray(list.nodes)) return null
  const named = (node: { name?: unknown } | undefined) => typeof node?.name === "string" && node.name ? node.name : null
  const own = list.nodes.find(node => node.id === sessionNodeId)
  if (own) return named(own)
  if (sessionNodeId === list.legacyNodeId || sessionNodeId === "local")
    return named(list.nodes.find(node => node.isLocal === true))
  return null
}
