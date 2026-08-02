import { useState, useEffect, useCallback, Fragment } from "react"
import { useParams, useNavigate } from "react-router-dom"
import {
  MasterDetailLayout,
  PanelHeader,
  ScrollArea,
  ItemListRow,
  Badge,
  Button,
} from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { useBreadcrumbLabel } from "@redbamboo/utility"
import { api } from "../lib/api"
import { useLocalSettings } from "../hooks/use-local-settings"
import { useAgents } from "../hooks/use-agents"
import { AgentPicker } from "../components/agent-picker"
import { setSettings } from "../lib/settings-store"

interface Automation {
  id: string
  name: string
  slug: string
  description: string
  enabled: boolean
  archivedAt?: string
  archivedReason?: string
  removeOnTrigger: boolean
  icon?: string
  triggerKind: string
  schedule?: string
  timezone: string
  misfirePolicy: string
  actionType: string
  actionConfig?: Record<string, unknown>
  reportToDiscussionId?: string
  definitionVersion: string
  lastRun?: string
  nextRun?: string
  lastStatus?: string
  lastJobId?: string
  lastResult?: string
  lastError?: string
  consecutiveFailures: number
  agentId?: string
  agentName?: string
  ownerApp?: string
  ownerPlugin?: string
}

interface AutomationRun {
  jobId: string
  status: string
  attemptCount: number
  queuedAt?: string
  startedAt?: string
  completedAt?: string
  errorMessage?: string
  resultJson?: string
  durationMs?: number
  costUsd?: number
}

interface TriggerResult {
  jobId: string
  status: string
  reused: boolean
}

const actionMeta: Record<string, { icon: string; label: string }> = {
  "ai-session": { icon: "ph-bold ph-brain", label: "AI Session" },
  "nova-session": { icon: "ph-bold ph-brain", label: "Nova Session" },
  "heartbeat-tick": { icon: "ph-bold ph-heartbeat", label: "Agent Pulse" },
  "flow-execution": { icon: "ph-bold ph-git-branch", label: "Workflow" },
  "http-check": { icon: "ph-bold ph-broadcast", label: "HTTP Watcher" },
  "http-action": { icon: "ph-bold ph-lightning", label: "HTTP Action" },
  "wallpaper-generate": { icon: "ph-bold ph-image", label: "Wallpaper" },
  "sonos-watchdog": { icon: "ph-bold ph-speaker-high", label: "Sonos Watchdog" },
  "backup": { icon: "ph-bold ph-archive", label: "Database Backup" },
  "builtin:backup": { icon: "ph-bold ph-archive", label: "Database Backup" },
}

function cronToHuman(cron?: string, triggerKind = "cron"): string {
  if (triggerKind === "manual" || !cron) return "Manual only"
  const parts = cron.trim().split(/\s+/)

  if (parts.length === 6) {
    const [sec, min, hour, dom, mon, dow] = parts
    if (sec?.startsWith("*/")) return `Every ${sec.slice(2)}s`
    if (min === "*" && hour === "*") return "Every minute"
    return cronFiveToHuman([min!, hour!, dom!, mon!, dow!])
  }

  if (parts.length === 5) return cronFiveToHuman(parts as [string, string, string, string, string])
  return cron
}

function formatCronTime(hour: string, min: string): string {
  const h = parseInt(hour)
  const m = parseInt(min)
  const period = h >= 12 ? "PM" : "AM"
  const h12 = h === 0 ? 12 : h > 12 ? h - 12 : h
  return `${h12}:${m.toString().padStart(2, "0")} ${period}`
}

const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const
const MONTH_NAMES = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"] as const

function cronFiveToHuman([min, hour, dom, mon, dow]: [string, string, string, string, string]): string {
  if (min.startsWith("*/")) return `Every ${min.slice(2)} min`
  if (hour.startsWith("*/")) return `Every ${hour.slice(2)}h`
  if (min === "0" && hour === "*") return "Every hour"

  const hasTime = hour !== "*" && min !== "*"
  const timeStr = hasTime ? formatCronTime(hour, min) : null

  if (dom === "*" && mon === "*" && dow !== "*") {
    const days = dow.split(",").map(d => DAY_NAMES[parseInt(d) % 7] ?? d).join(", ")
    return timeStr ? `${days} at ${timeStr}` : days
  }

  if (dom !== "*" && mon === "*" && dow === "*") {
    const ordinal = dom === "1" || dom === "21" || dom === "31" ? `${dom}st`
      : dom === "2" || dom === "22" ? `${dom}nd`
      : dom === "3" || dom === "23" ? `${dom}rd`
      : `${dom}th`
    return timeStr ? `Monthly on the ${ordinal} at ${timeStr}` : `Monthly on the ${ordinal}`
  }

  if (dom !== "*" && mon !== "*" && dow === "*") {
    const monthName = MONTH_NAMES[parseInt(mon) - 1] ?? mon
    return timeStr ? `${monthName} ${dom} at ${timeStr}` : `${monthName} ${dom}`
  }

  if (dom === "*" && mon === "*" && dow === "*" && timeStr) return `Daily at ${timeStr}`
  return `${min} ${hour} ${dom} ${mon} ${dow}`
}

function formatTime(iso?: string): string | null {
  if (!iso) return null
  const date = new Date(iso)
  if (isNaN(date.getTime())) return null
  return date.toLocaleString(undefined, {
    month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
  })
}

function formatDuration(ms?: number): string | null {
  if (ms == null) return null
  if (ms < 1000) return `${ms}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60_000)}m ${Math.floor((ms % 60_000) / 1000)}s`
}

function getIcon(automation: Automation): string {
  if (automation.icon) return automation.icon
  return (actionMeta[automation.actionType] ?? { icon: "ph-bold ph-gear" }).icon
}

function isSystemAutomation(automation: Automation): boolean {
  return automation.name.startsWith("system:")
    || automation.ownerApp === "redleaf"
    || automation.ownerApp === "system"
    || automation.actionType === "backup"
    || automation.actionType === "builtin:backup"
}

function canRetire(automation: Automation): boolean {
  if (automation.archivedAt || isSystemAutomation(automation)) return false
  if (automation.actionType === "flow-execution") return false
  return !automation.ownerApp || automation.ownerApp === "nova"
}

function statusIcon(status?: string): string {
  if (status === "Completed") return "ph-check-circle text-green-500"
  if (status === "Failed" || status === "TimedOut") return "ph-warning text-red-500"
  if (status === "Skipped" || status === "Cancelled") return "ph-minus-circle text-text-disabled"
  if (status === "Running") return "ph-spinner animate-spin text-amber-500"
  return "ph-clock text-text-disabled"
}

const configLabels: Record<string, string> = {
  prompt: "Prompt",
  systemPromptHint: "System hint",
  url: "URL",
  method: "Method",
  condition: "Condition",
}

function ConfigDisplay({ config }: { config: Record<string, unknown> }) {
  const entries = Object.entries(config).filter(([, value]) => value !== undefined && value !== null)
  return (
    <div className="space-y-2">
      {entries.map(([key, value]) => {
        const label = configLabels[key] ?? key
        const isLongText = typeof value === "string" && value.length > 80
        if (typeof value === "object") {
          return (
            <div key={key}>
              <div className="text-[11px] text-text-muted mb-1">{label}</div>
              <pre className="bg-overlay-5 rounded-md px-3 py-2 text-xs font-mono whitespace-pre-wrap overflow-auto">
                {JSON.stringify(value, null, 2)}
              </pre>
            </div>
          )
        }
        if (isLongText) {
          return (
            <div key={key}>
              <div className="text-[11px] text-text-muted mb-1">{label}</div>
              <div className="bg-overlay-5 rounded-md px-3 py-2 text-sm leading-relaxed markdown-body">
                <MarkdownRenderer content={String(value)} />
              </div>
            </div>
          )
        }
        return (
          <div key={key} className="flex items-baseline gap-3 text-xs">
            <span className="text-text-muted shrink-0">{label}</span>
            <span className={`font-mono ${key === "url" ? "text-primary" : ""}`}>{String(value)}</span>
          </div>
        )
      })}
    </div>
  )
}

function RunHistory({ runs, loading }: { runs: AutomationRun[]; loading: boolean }) {
  return (
    <div>
      <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
        Compute attempts
      </div>
      {loading ? (
        <div className="text-xs text-text-muted py-2">Loading attempts...</div>
      ) : runs.length === 0 ? (
        <div className="bg-overlay-5 rounded-md p-3 text-xs text-text-muted">No canonical attempts yet. Legacy history remains preserved in its original streams.</div>
      ) : (
        <div className="rounded-md border border-overlay-10 divide-y divide-overlay-10 overflow-hidden">
          {runs.map(run => (
            <a
              key={run.jobId}
              href={`/apps/compute-dashboard/jobs?select=${encodeURIComponent(run.jobId)}`}
              className="flex items-center gap-3 px-3 py-2.5 hover:bg-overlay-5 transition-colors"
            >
              <i className={`ph-bold ${statusIcon(run.status)} text-sm`} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2 text-xs">
                  <span className="font-medium">{run.status}</span>
                  {run.attemptCount > 1 && <span className="text-text-muted">claim {run.attemptCount}</span>}
                  {formatDuration(run.durationMs) && <span className="text-text-disabled">{formatDuration(run.durationMs)}</span>}
                </div>
                <div className="font-mono text-[10px] text-text-disabled truncate">{run.jobId}</div>
              </div>
              <span className="text-[11px] text-text-muted shrink-0">{formatTime(run.startedAt ?? run.queuedAt)}</span>
              <i className="ph-bold ph-arrow-square-out text-xs text-text-disabled" />
            </a>
          ))}
        </div>
      )}
    </div>
  )
}

function AutomationDetail({ automation, runs, runsLoading, onRetire, onTrigger, triggering }: {
  automation: Automation
  runs: AutomationRun[]
  runsLoading: boolean
  onRetire: () => void
  onTrigger: () => void
  triggering: boolean
}) {
  const meta = actionMeta[automation.actionType] ?? { icon: "ph-bold ph-gear", label: automation.actionType }
  const system = isSystemAutomation(automation)
  const managedByFlow = automation.actionType === "flow-execution"

  return (
    <div className="h-full flex flex-col">
      <PanelHeader title={system ? automation.name.replace("system:", "") : automation.name}>
        <Button variant="ghost" size="xs" onClick={onTrigger} disabled={triggering || !!automation.archivedAt} title="Create a Compute attempt and run now">
          <i className={`ph-bold ${triggering ? "ph-spinner animate-spin" : "ph-play"} text-xs mr-1`} />
          {triggering ? "Starting" : "Run now"}
        </Button>
        {canRetire(automation) && (
          <Button variant="ghost" size="xs" onClick={onRetire} title="Disable and retain this versioned definition and its history">
            <i className="ph-bold ph-archive text-xs mr-1" />
            Retire
          </Button>
        )}
      </PanelHeader>

      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        <div className="flex items-center gap-2 flex-wrap">
          <i className={`${getIcon(automation)} text-sm text-primary`} />
          <span className="text-sm font-medium">{meta.label}</span>
          {automation.archivedAt ? <Badge variant="secondary">Retired</Badge>
            : automation.enabled ? <Badge variant="default">Active</Badge>
              : <Badge variant="secondary">Disabled</Badge>}
          {automation.removeOnTrigger && <Badge variant="secondary">One-shot</Badge>}
          {managedByFlow && <Badge variant="outline">Managed by workflow</Badge>}
        </div>

        {automation.description && <p className="text-sm text-text-muted leading-relaxed">{automation.description}</p>}
        {automation.archivedReason && <p className="text-xs text-text-muted">Retired: {automation.archivedReason}</p>}

        <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2.5 text-xs">
          <span className="text-text-muted">Schedule</span>
          <span>
            {cronToHuman(automation.schedule, automation.triggerKind)}
            {automation.schedule && <span className="text-text-disabled ml-2">({automation.schedule})</span>}
          </span>
          <span className="text-text-muted">Timezone</span>
          <span>{automation.timezone}</span>
          <span className="text-text-muted">Misfires</span>
          <span className="capitalize">{automation.misfirePolicy}</span>
          {(automation.agentName || automation.agentId) && <>
            <span className="text-text-muted">Agent</span>
            <span>{automation.agentName ?? automation.agentId}</span>
          </>}
          {(automation.ownerApp || automation.ownerPlugin) && <>
            <span className="text-text-muted">Owner</span>
            <span>{automation.ownerPlugin ?? automation.ownerApp}</span>
          </>}
          {automation.reportToDiscussionId && <>
            <span className="text-text-muted">Reports to</span>
            <span className="font-mono">Discussion {automation.reportToDiscussionId}</span>
          </>}
          {automation.lastRun && <>
            <span className="text-text-muted">Last run</span>
            <span className="flex items-center gap-1.5">
              <i className={`ph-bold ${statusIcon(automation.lastStatus)} text-xs`} />
              {formatTime(automation.lastRun)}{automation.lastStatus ? `, ${automation.lastStatus}` : ""}
            </span>
          </>}
          {automation.nextRun && !automation.archivedAt && <>
            <span className="text-text-muted">Next run</span>
            <span>{formatTime(automation.nextRun)}</span>
          </>}
          <span className="text-text-muted">Definition</span>
          <span className="font-mono text-[10px] truncate" title={automation.definitionVersion}>{automation.definitionVersion}</span>
        </div>

        {automation.lastJobId && (
          <a href={`/apps/compute-dashboard/jobs?select=${encodeURIComponent(automation.lastJobId)}`} className="flex items-center gap-2 rounded-md bg-overlay-5 px-3 py-2 text-xs hover:bg-overlay-10">
            <i className="ph-bold ph-fingerprint text-primary" />
            <span>Latest Compute attempt</span>
            <span className="font-mono text-text-muted truncate">{automation.lastJobId}</span>
            <i className="ph-bold ph-arrow-square-out ml-auto" />
          </a>
        )}

        {automation.actionConfig && Object.keys(automation.actionConfig).length > 0 && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">Configuration</div>
            <ConfigDisplay config={automation.actionConfig} />
          </div>
        )}

        {automation.lastError && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">Last error</div>
            <div className="bg-overlay-5 rounded-md p-3 space-y-2">
              <div className="flex items-center gap-2 text-xs">
                <i className="ph-bold ph-warning text-amber-500" />
                <span className="font-medium">{automation.consecutiveFailures ? `${automation.consecutiveFailures} consecutive failure(s)` : "Last attempt failed"}</span>
              </div>
              <div className="text-sm leading-relaxed markdown-body"><MarkdownRenderer content={automation.lastError} /></div>
            </div>
          </div>
        )}

        <RunHistory runs={runs} loading={runsLoading} />
      </div>
    </div>
  )
}

export function AutomationsPanel() {
  const { automationId: urlAutomationId } = useParams()
  const navigate = useNavigate()
  const [automations, setAutomations] = useState<Automation[]>([])
  const [runs, setRuns] = useState<AutomationRun[]>([])
  const [runsLoading, setRunsLoading] = useState(false)
  const [mobileTab, setMobileTab] = useState(0)
  const [triggering, setTriggering] = useState<string | null>(null)
  const { agents, defaultAgentId } = useAgents()
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter
  const multiAgent = agents.length > 1
  const isNovaSelected = !agentFilter || agentFilter === defaultAgentId

  const selected = automations.find(a => a.id === urlAutomationId)
    ?? automations.find(a => a.name === urlAutomationId || a.slug === urlAutomationId)
    ?? null

  useBreadcrumbLabel(
    urlAutomationId ? `/apps/nova/pulse/${urlAutomationId}` : undefined,
    selected?.name ?? urlAutomationId,
  )

  const refresh = useCallback(async () => {
    const data = await api.get<{ items: Automation[] }>("/api/automations/status")
    setAutomations(data.items)
  }, [])

  const refreshRuns = useCallback(async (automationId: string) => {
    setRunsLoading(true)
    try {
      const data = await api.get<{ items: AutomationRun[] }>(`/api/automations/${automationId}/runs?limit=25`)
      setRuns(data.items)
    } catch {
      setRuns([])
    } finally {
      setRunsLoading(false)
    }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  useEffect(() => {
    if (!selected) {
      setRuns([])
      return
    }
    if (urlAutomationId !== selected.id) {
      navigate(`/apps/nova/pulse/${selected.id}`, { replace: true })
      return
    }
    refreshRuns(selected.id)
  }, [selected?.id, urlAutomationId, navigate, refreshRuns])

  const handleRetire = useCallback(async (automation: Automation) => {
    await api.patch(`/api/entities/${automation.id}`, {
      data: {
        enabled: false,
        archived_at: new Date().toISOString(),
        archived_reason: "Retired from Nova Pulse",
      },
    })
    await refresh()
  }, [refresh])

  const handleTrigger = useCallback(async (automation: Automation) => {
    setTriggering(automation.id)
    try {
      await api.post<TriggerResult>(`/api/automations/${automation.id}/trigger`)
      await Promise.all([refresh(), refreshRuns(automation.id)])
    } finally {
      setTriggering(prev => prev === automation.id ? null : prev)
    }
  }, [refresh, refreshRuns])

  const handleSelect = useCallback((automationId: string) => {
    navigate(`/apps/nova/pulse/${automationId}`)
    setMobileTab(1)
  }, [navigate])

  const matchesAgent = (automation: Automation) => {
    if (!agentFilter) return true
    if (agentFilter === defaultAgentId) return !automation.agentId || automation.agentId === defaultAgentId
    return automation.agentId === agentFilter
  }

  const visible = automations.filter(matchesAgent)
  const active = visible.filter(a => !a.archivedAt && !isSystemAutomation(a))
  const system = visible.filter(a => !a.archivedAt && isSystemAutomation(a))
  const retired = visible.filter(a => !!a.archivedAt)

  const renderRow = useCallback((automation: Automation, muted = false) => {
    const meta = actionMeta[automation.actionType] ?? { icon: "ph-bold ph-gear", label: automation.actionType }
    return (
      <ItemListRow
        selected={automation.id === selected?.id}
        icon={<i className={`${getIcon(automation)} text-xs ${muted || !automation.enabled ? "text-text-disabled" : "text-primary"}`} />}
        title={isSystemAutomation(automation) ? automation.name.replace("system:", "") : automation.name}
        subtitle={cronToHuman(automation.schedule, automation.triggerKind)}
        badge={<>
          <Badge variant="outline">{meta.label}</Badge>
          {automation.lastStatus && <Badge variant="secondary">{automation.lastStatus}</Badge>}
        </>}
        onClick={() => handleSelect(automation.id)}
      />
    )
  }, [selected?.id, handleSelect])

  const section = (label: string, items: Automation[], muted = false) => items.length > 0 && (
    <>
      <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">{label}</div>
      {items.map(automation => <Fragment key={automation.id}>{renderRow(automation, muted)}</Fragment>)}
    </>
  )

  const sidebar = (
    <>
      <PanelHeader title="Pulse">
        {multiAgent && <AgentPicker agents={agents} selectedId={agentFilter} onSelect={id => setSettings({ agentFilter: id })} showAll />}
      </PanelHeader>
      <ScrollArea className="flex-1">
        {visible.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="ph-bold ph-heartbeat text-2xl mb-3 opacity-30" />
              <p className="text-sm">No routines{!isNovaSelected ? " for this agent" : " yet"}</p>
              {isNovaSelected && <p className="text-xs text-text-disabled mt-1">Ask Nova to set one up in chat</p>}
            </div>
          </div>
        ) : (
          <div className="flex flex-col">
            {section("Automations", active)}
            {section("System", system, true)}
            {section("Retired", retired, true)}
          </div>
        )}
      </ScrollArea>
    </>
  )

  const detail = selected ? (
    <AutomationDetail
      automation={selected}
      runs={runs}
      runsLoading={runsLoading}
      onRetire={() => handleRetire(selected)}
      onTrigger={() => handleTrigger(selected)}
      triggering={triggering === selected.id}
    />
  ) : (
    <div className="h-full flex items-center justify-center text-text-muted">
      <div className="text-center">
        <i className="ph-bold ph-heartbeat text-2xl mb-3 opacity-30" />
        <p className="text-sm">Select a routine to view details</p>
      </div>
    </div>
  )

  return (
    <MasterDetailLayout
      layoutKey="nova-automations"
      mobileLabels={["Pulse", "Detail"]}
      mobileTab={mobileTab}
      onMobileTabChange={tab => {
        setMobileTab(tab)
        if (tab === 0) navigate("/apps/nova/pulse")
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
