import { useEffect, useState } from "react"
import { sessionInfoNodeFrom, type SessionInfoMemory, type SessionInfoNode, type SessionInfoSkills, type SessionInfoWorkspace } from "@redbamboo/chat"
import { getEntityHref } from "@redbamboo/utility"
import { api } from "../lib/api"
import { agentMemory, agentSkills, entityData, parseMood, MOOD_PATH, type EntityLike } from "../lib/session-info-sources"

export interface SessionInfoSources {
  skills: SessionInfoSkills | null
  node: SessionInfoNode | null
  workspace: SessionInfoWorkspace | null
  memory: SessionInfoMemory | null
}

const NONE: SessionInfoSources = { skills: null, node: null, workspace: null, memory: null }

const normalizePath = (path: string | null | undefined) => path ? path.replaceAll("\\", "/") : null

/**
 * What the Info modal shows beyond the session itself: the agent's skills, its workspace and memory
 * mood, and the node the session runs on. Read each time the modal opens, from RedLeaf's own
 * entities, memory file and node list. A source that fails to answer leaves its tab out.
 */
export function useSessionInfoSources(args: {
  open: boolean
  agentId: string | null | undefined
  /** The agent's workspace entity (AgentInfo.workspaceId). */
  workspaceId: string | null | undefined
  /** The node the session runs on, as the session says. */
  sessionNodeId: string | null
  /** The folder the session works in: the agent's workspace on that node. */
  folder: string | null | undefined
}): SessionInfoSources {
  const { open, agentId, workspaceId, sessionNodeId, folder } = args
  const [sources, setSources] = useState<SessionInfoSources>(NONE)

  useEffect(() => {
    if (!open || !agentId) { setSources(NONE); return }
    const controller = new AbortController()
    const { signal } = controller
    const attempt = <T,>(request: Promise<T>) => request.then(value => value, () => null)

    void (async () => {
      const [agent, skillList, workspaceEntity, mood, nodes] = await Promise.all([
        attempt(api.get<EntityLike>(`/api/entities/${encodeURIComponent(agentId)}`, signal)),
        attempt(api.get<{ items: EntityLike[] }>("/api/entities?type=agent-skill&limit=500", signal)),
        workspaceId ? attempt(api.get<EntityLike>(`/api/entities/${encodeURIComponent(workspaceId)}`, signal)) : Promise.resolve(null),
        workspaceId
          ? attempt(api.get<{ content?: string }>(`/api/apps/nova/memory/file?agent=${encodeURIComponent(agentId)}&path=${encodeURIComponent(MOOD_PATH)}`, signal))
          : Promise.resolve(null),
        // The kernel's node list carries each node's icon and colour but needs a signed-in person; RedCompute's
        // answers anyone, without them.
        attempt(api.get<unknown>("/api/nodes", signal).catch(() => api.get<unknown>("/compute/nodes", signal))),
      ])
      if (signal.aborted) return

      const items = agent && skillList ? agentSkills(agent, skillList.items) : []
      const agentHref = getEntityHref("agent", agentId)
      const workspaceHref = workspaceId ? `/workspace/${encodeURIComponent(workspaceId)}` : null
      const folderPath = normalizePath(folder)
      setSources({
        skills: items.length > 0 ? { items, manageHref: agentHref, manageLabel: "Manage on Nova" } : null,
        node: sessionInfoNodeFrom(nodes, sessionNodeId, folderPath),
        workspace: workspaceEntity?.name ? {
          kind: "workspace",
          entity: { id: workspaceEntity.id, typeSlug: "page", name: workspaceEntity.name, href: workspaceHref },
          icon: "ph-bold ph-folder-simple",
          subtitle: typeof entityData(workspaceEntity).description === "string" ? String(entityData(workspaceEntity).description) : null,
          summary: workspaceEntity.name,
          rows: folderPath ? [{ label: "Folder", value: folderPath, mono: true }] : [],
        } : null,
        memory: workspaceEntity ? agentMemory(parseMood(mood?.content), workspaceHref) : null,
      })
    })()

    return () => controller.abort()
  }, [open, agentId, workspaceId, sessionNodeId, folder])

  return sources
}
