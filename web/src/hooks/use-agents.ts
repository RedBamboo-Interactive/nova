import { useState, useEffect, useCallback, useRef } from "react"
import { useWsSubscribe } from "@redbamboo/utility"
import { useUiEnvironment } from "@redbamboo/ui"
import { api } from "../lib/api"
import { AgentDirectorySync, EMPTY_AGENT_DIRECTORY } from "../lib/agent-directory-sync"
import type { AgentInfo } from "../lib/types"

export function useAgents() {
  const environment = useUiEnvironment()
  const [{ agents, avatarRevision }, setSnapshot] = useState(EMPTY_AGENT_DIRECTORY)
  const sync = useRef<AgentDirectorySync | null>(null)

  useEffect(() => {
    const directory = new AgentDirectorySync(
      () => api.get<AgentInfo[]>("/api/apps/nova/agents"),
      setSnapshot,
    )
    sync.current = directory

    // An outfit change replaces the effective avatar URL on the agent. Busting
    // the old image URL is not enough: every avatar consumer needs fresh agent
    // data so chat messages, the discussion list, and pickers all move together.
    // The live event is not replayed, so foreground return and reconnect re-read.
    const detach = directory.attach({
      window: environment.window,
      document: environment.document,
      avatarEvents: window,
    })
    void directory.refresh()
    return () => {
      detach()
      directory.dispose()
      if (sync.current === directory) sync.current = null
    }
  }, [environment.window, environment.document])

  useWsSubscribe(event => sync.current?.handleWsEvent(event.type))

  const refreshAgents = useCallback(() => {
    void sync.current?.refresh()
  }, [])

  const getAgent = useCallback(
    (id: string | null) => (id ? agents.find((a) => a.id === id) : undefined),
    [agents],
  )

  const defaultAgentId = agents.find((a) => a.slug === "nova")?.id ?? agents[0]?.id ?? null

  return { agents, getAgent, defaultAgentId, refreshAgents, avatarRevision }
}
