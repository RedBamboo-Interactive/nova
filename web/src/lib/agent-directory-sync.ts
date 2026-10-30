import type { AgentInfo } from "./types.ts"
import { LatestTaskCoordinator } from "./latest-task-coordinator.ts"

/** Dispatched on the opener window by the live bridge and by the Outfits panel. */
export const AVATAR_CHANGED_EVENT = "nova:avatar-changed"

interface ListenerTarget {
  addEventListener(type: string, listener: () => void): void
  removeEventListener(type: string, listener: () => void): void
}

export interface AgentDirectoryEnvironment {
  window: ListenerTarget
  document: ListenerTarget & { readonly visibilityState: string }
  /** Where {@link AVATAR_CHANGED_EVENT} arrives. In Float this is still the opener window. */
  avatarEvents: ListenerTarget
}

export interface AgentDirectorySnapshot {
  agents: AgentInfo[]
  /** Advances whenever a later snapshot changes an Agent's avatar URL. */
  avatarRevision: number
}

export const EMPTY_AGENT_DIRECTORY: AgentDirectorySnapshot = { agents: [], avatarRevision: 0 }

export function isConnectionRecovered(eventType: string): boolean {
  return eventType === "websocket.connected" || eventType === "upstream.connected"
}

export function avatarsChanged(previous: readonly AgentInfo[], next: readonly AgentInfo[]): boolean {
  const before = new Map(previous.map(agent => [agent.id, agent.avatarUrl]))
  return next.some(agent => before.has(agent.id) && before.get(agent.id) !== agent.avatarUrl)
}

/** Re-keys a portrait URL after an avatar change so a same-URL fallback cannot stay cached. */
export function versionedAvatarSrc(base: string, avatarRevision: number): string {
  if (!avatarRevision) return base
  return `${base}${base.includes("?") ? "&" : "?"}v=${avatarRevision}`
}

/**
 * Keeps one client's Agent list on the authoritative `/api/apps/nova/agents` snapshot.
 *
 * Live `agent.avatar-changed` frames are not replayed: a phone that sleeps through
 * one reconnects to a fresh socket and never hears about the outfit change. The
 * moments this client has reason to distrust its event history (coming back to a
 * visible page, a socket that just reconnected) therefore re-read the snapshot.
 * Reads are serialized and coalesced, because a mobile resume emits visibility,
 * pageshow, focus and reconnect signals together.
 */
export class AgentDirectorySync {
  private current: AgentDirectorySnapshot = EMPTY_AGENT_DIRECTORY
  private loaded = false
  private disposed = false
  private readonly reads = new LatestTaskCoordinator<"agents">()
  private readonly load: () => Promise<AgentInfo[]>
  private readonly onChange: (snapshot: AgentDirectorySnapshot) => void

  constructor(
    load: () => Promise<AgentInfo[]>,
    onChange: (snapshot: AgentDirectorySnapshot) => void,
  ) {
    this.load = load
    this.onChange = onChange
  }

  get snapshot(): AgentDirectorySnapshot {
    return this.current
  }

  refresh(): Promise<void> {
    if (this.disposed) return Promise.resolve()
    return this.reads.run("agents", async () => {
      if (this.disposed) return
      let agents: AgentInfo[]
      try {
        agents = await this.load()
      } catch {
        // A failed read is not evidence that the Agents changed; keep what we have.
        return
      }
      if (this.disposed || !Array.isArray(agents)) return
      if (this.loaded && JSON.stringify(agents) === JSON.stringify(this.current.agents)) return
      const avatarRevision = this.loaded && avatarsChanged(this.current.agents, agents)
        ? this.current.avatarRevision + 1
        : this.current.avatarRevision
      this.loaded = true
      this.current = { agents, avatarRevision }
      this.onChange(this.current)
    }).catch(() => {})
  }

  handleWsEvent(eventType: string): void {
    if (isConnectionRecovered(eventType)) void this.refresh()
  }

  attach(environment: AgentDirectoryEnvironment): () => void {
    const foreground = () => {
      if (environment.document.visibilityState !== "hidden") void this.refresh()
    }
    const avatarChanged = () => { void this.refresh() }
    environment.window.addEventListener("focus", foreground)
    environment.document.addEventListener("visibilitychange", foreground)
    environment.avatarEvents.addEventListener(AVATAR_CHANGED_EVENT, avatarChanged)
    return () => {
      environment.window.removeEventListener("focus", foreground)
      environment.document.removeEventListener("visibilitychange", foreground)
      environment.avatarEvents.removeEventListener(AVATAR_CHANGED_EVENT, avatarChanged)
    }
  }

  dispose(): void {
    this.disposed = true
  }
}
