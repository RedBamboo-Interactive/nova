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

// Kernel automation ENTITY, flattened. Automations moved to the kernel with the
// Leaf migration — this panel is a viewer over /api/entities?type=automation plus
// the kernel's manual-trigger endpoint.
interface Automation {
  id: string
  name: string
  description: string
  schedule: string
  enabled: boolean
  removeOnTrigger: boolean
  icon?: string
  agentId?: string
  actionType: string
  prompt?: string
  actionConfig?: Record<string, unknown>
  reportToDiscussionId?: string
  lastRun?: string
  nextRun?: string
  lastError?: string
  lastResult?: string
  consecutiveFailures?: number
}

function parseEntityData(raw: unknown): Record<string, unknown> {
  if (typeof raw === "string") {
    try { return JSON.parse(raw) as Record<string, unknown> } catch { return {} }
  }
  return (raw as Record<string, unknown>) ?? {}
}

function mapAutomationEntity(e: { id: string; name: string; data: unknown }): Automation {
  const d = parseEntityData(e.data)
  const str = (k: string) => (typeof d[k] === "string" ? (d[k] as string) : undefined)
  return {
    id: e.id,
    name: e.name,
    description: str("description") ?? "",
    schedule: str("schedule") ?? "",
    enabled: d.enabled !== false && d.enabled !== "false",
    removeOnTrigger: d.remove_on_trigger === true || d.remove_on_trigger === "true",
    icon: str("icon"),
    agentId: str("agent"),
    actionType: str("action_type") ?? "ai-session",
    prompt: str("prompt"),
    actionConfig: typeof d.action_config === "object" && d.action_config !== null
      ? (d.action_config as Record<string, unknown>)
      : undefined,
    reportToDiscussionId: str("report_to_discussion_id"),
    lastRun: str("last_run"),
    nextRun: str("next_run"),
    lastError: str("last_error"),
    lastResult: str("last_result"),
    consecutiveFailures: typeof d.consecutive_failures === "number" ? d.consecutive_failures : undefined,
  }
}

const actionMeta: Record<string, { icon: string; label: string }> = {
  "ai-session":     { icon: "ph-bold ph-brain",          label: "AI Session" },
  "nova-session":   { icon: "ph-bold ph-brain",          label: "Nova Session" },
  "http-check":     { icon: "ph-bold ph-broadcast",  label: "HTTP Watcher" },
  "http-action":    { icon: "ph-bold ph-lightning",           label: "HTTP Action" },
  "builtin:backup": { icon: "ph-bold ph-archive",    label: "System" },
}

function cronToHuman(cron: string): string {
  const parts = cron.trim().split(/\s+/)

  if (parts.length === 6) {
    const [sec, min, hour, dom, mon, dow] = parts
    if (sec?.startsWith("*/")) return `Every ${sec.slice(2)}s`
    if (min === "*" && hour === "*") return `Every minute`
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
    return timeStr ? `${days} at ${timeStr}` : `${days}`
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

  if (dom === "*" && mon === "*" && dow === "*" && timeStr) {
    return `Daily at ${timeStr}`
  }

  return `${min} ${hour} ${dom} ${mon} ${dow}`
}

function formatTime(iso?: string): string | null {
  if (!iso) return null
  const d = new Date(iso)
  if (isNaN(d.getTime())) return null
  return d.toLocaleString(undefined, {
    month: "short", day: "numeric",
    hour: "2-digit", minute: "2-digit",
  })
}

const configLabels: Record<string, string> = {
  prompt: "Prompt",
  systemPromptHint: "System hint",
  url: "URL",
  method: "Method",
  condition: "Condition",
}

function ConfigDisplay({ config }: { config: Record<string, unknown> }) {
  const entries = Object.entries(config)

  return (
    <div className="space-y-2">
      {entries.map(([key, value]) => {
        const label = configLabels[key] ?? key
        const isLongText = typeof value === "string" && value.length > 80

        if (typeof value === "object" && value !== null) {
          return (
            <div key={key}>
              <div className="text-[11px] text-text-muted mb-1">{label}</div>
              <div className="bg-overlay-5 rounded-md px-3 py-2 text-xs grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                {Object.entries(value as Record<string, unknown>).map(([k, v]) => (
                  <Fragment key={k}>
                    <span className="text-text-muted">{k}</span>
                    <span className="font-mono">{String(v)}</span>
                  </Fragment>
                ))}
              </div>
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

function getIcon(a: Automation): string {
  if (a.icon) return a.icon
  return (actionMeta[a.actionType] ?? { icon: "ph-bold ph-gear" }).icon
}

function AutomationDetail({ automation, onDelete, onTrigger, triggering }: {
  automation: Automation
  onDelete: () => void
  onTrigger: () => void
  triggering: boolean
}) {
  const meta = actionMeta[automation.actionType] ?? { icon: "ph-bold ph-gear", label: automation.actionType }
  const isSystem = automation.name.startsWith("system:")

  return (
    <div className="h-full flex flex-col">
      <PanelHeader title={isSystem ? automation.name.replace("system:", "") : automation.name}>
        <Button
          variant="ghost"
          size="xs"
          onClick={onTrigger}
          disabled={triggering}
          title="Run this automation now (runs in the background)"
        >
          <i className={`ph-bold ${triggering ? "ph-spinner animate-spin" : "ph-play"} text-xs mr-1`} />
          {triggering ? "Started" : "Run now"}
        </Button>
        {!isSystem && (
          <Button
            variant="ghost"
            size="icon-xs"
            onClick={onDelete}
            title="Delete automation"
          >
            <i className="ph-bold ph-trash text-xs" />
          </Button>
        )}
      </PanelHeader>

      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        <div className="flex items-center gap-2">
          <i className={`${getIcon(automation)} text-sm text-primary`} />
          <span className="text-sm font-medium">{meta.label}</span>
          {automation.enabled ? (
            <Badge variant="default">Active</Badge>
          ) : (
            <Badge variant="secondary">Disabled</Badge>
          )}
          {automation.removeOnTrigger && <Badge variant="secondary">One-shot</Badge>}
        </div>

        {automation.description && (
          <p className="text-sm text-text-muted leading-relaxed">{automation.description}</p>
        )}

        <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2.5 text-xs">
          <span className="text-text-muted">Schedule</span>
          <span>
            {cronToHuman(automation.schedule)}
            <span className="text-text-disabled ml-2">({automation.schedule})</span>
          </span>

          {automation.reportToDiscussionId && (
            <>
              <span className="text-text-muted">Reports to</span>
              <span className="font-mono text-xs">Discussion {automation.reportToDiscussionId}</span>
            </>
          )}

          {automation.lastRun && (
            <>
              <span className="text-text-muted">Last run</span>
              <span>{formatTime(automation.lastRun)}</span>
            </>
          )}

          {automation.nextRun && (
            <>
              <span className="text-text-muted">Next run</span>
              <span>{formatTime(automation.nextRun)}</span>
            </>
          )}
        </div>

        {(automation.prompt || (automation.actionConfig && Object.keys(automation.actionConfig).length > 0)) && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
              Configuration
            </div>
            <ConfigDisplay config={{
              ...(automation.prompt ? { prompt: automation.prompt } : {}),
              ...automation.actionConfig,
            }} />
          </div>
        )}

        {automation.lastResult && !automation.lastError && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
              Last Result
            </div>
            <div className="bg-overlay-5 rounded-md p-3 space-y-2">
              <div className="flex items-center gap-2 text-xs">
                <i className="ph-bold ph-check-circle text-green-500" />
                <span className="font-medium">Succeeded</span>
                {automation.lastRun && (
                  <span className="text-text-muted ml-auto">{formatTime(automation.lastRun)}</span>
                )}
              </div>
              <div className="text-sm leading-relaxed markdown-body">
                <MarkdownRenderer content={automation.lastResult} />
              </div>
            </div>
          </div>
        )}

        {automation.lastError && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
              Last Error
            </div>
            <div className="bg-overlay-5 rounded-md p-3 space-y-2">
              <div className="flex items-center gap-2 text-xs">
                <i className="ph-bold ph-warning text-amber-500" />
                <span className="font-medium">
                  {automation.consecutiveFailures ? `${automation.consecutiveFailures} consecutive failure(s)` : "Last run failed"}
                </span>
              </div>
              <div className="text-sm leading-relaxed markdown-body">
                <MarkdownRenderer content={automation.lastError} />
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export function AutomationsPanel() {
  const { automationName: urlAutomationName } = useParams()
  const navigate = useNavigate()
  const [automations, setAutomations] = useState<Automation[]>([])
  const [mobileTab, setMobileTab] = useState(0)
  const { agents, defaultAgentId } = useAgents()
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter
  const multiAgent = agents.length > 1
  const isNovaSelected = !agentFilter || agentFilter === defaultAgentId

  const selectedName = urlAutomationName ?? null

  useBreadcrumbLabel(
    urlAutomationName ? `/apps/nova/pulse/${urlAutomationName}` : undefined,
    urlAutomationName ?? undefined,
  )

  const refresh = useCallback(async () => {
    const data = await api.get<{ items: Array<{ id: string; name: string; data: unknown }> }>(
      "/api/entities?type=automation&limit=200")
    setAutomations(data.items.map(mapAutomationEntity))
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const selected = automations.find((a) => a.name === selectedName) ?? null

  const userAutomations = automations.filter((a) => !a.name.startsWith("system:"))
  const systemAutomations = automations.filter((a) => a.name.startsWith("system:"))

  const handleDelete = useCallback(async (a: Automation) => {
    await api.delete(`/api/entities/${a.id}`)
    if (selectedName === a.name) navigate("/apps/nova/pulse")
    refresh()
  }, [selectedName, refresh, navigate])

  const [triggering, setTriggering] = useState<string | null>(null)
  const handleTrigger = useCallback(async (a: Automation) => {
    setTriggering(a.name)
    try {
      await api.post(`/api/automations/${a.id}/trigger`)
    } catch { /* best effort — outcome lands on the entity's last_run/last_error */ }
    setTimeout(() => {
      setTriggering((prev) => (prev === a.name ? null : prev))
      refresh()
    }, 1500)
  }, [refresh])

  const handleSelect = useCallback((name: string) => {
    navigate(`/apps/nova/pulse/${encodeURIComponent(name)}`)
    setMobileTab(1)
  }, [navigate])

  const renderRow = useCallback((a: Automation, isSystem = false) => {
    const meta = actionMeta[a.actionType] ?? { icon: "ph-bold ph-gear", label: a.actionType }
    return (
      <ItemListRow
        selected={a.name === selectedName}
        icon={
          <i className={`${getIcon(a)} text-xs ${isSystem ? "text-text-muted" : a.enabled ? "text-primary" : "text-text-disabled"}`} />
        }
        title={isSystem ? a.name.replace("system:", "") : a.name}
        subtitle={cronToHuman(a.schedule)}
        badge={
          !isSystem ? (
            <>
              <Badge variant="outline">{meta.label}</Badge>
              {a.removeOnTrigger && <Badge variant="secondary">once</Badge>}
            </>
          ) : undefined
        }
        onClick={() => handleSelect(a.name)}
        trailing={
          !isSystem ? (
            <Button
              variant="ghost"
              size="icon-xs"
              className="opacity-0 group-hover/row:opacity-100 transition-opacity"
              onClick={async (e) => {
                e.stopPropagation()
                handleDelete(a)
              }}
            >
              <i className="ph-bold ph-x text-xs" />
            </Button>
          ) : undefined
        }
      />
    )
  }, [selectedName, handleSelect, handleDelete])

  const matchesAgent = (a: Automation) => {
    if (!agentFilter) return true
    if (agentFilter === defaultAgentId)
      return !a.agentId || a.agentId === defaultAgentId
    return a.agentId === agentFilter
  }
  const visibleAutomations = automations.filter(matchesAgent)
  const visibleUser = userAutomations.filter(matchesAgent)
  const visibleSystem = systemAutomations.filter(matchesAgent)

  const sidebar = (
    <>
      <PanelHeader title={multiAgent ? undefined : "Pulse"}>
        {multiAgent && (
          <AgentPicker
            agents={agents}
            selectedId={agentFilter}
            onSelect={(id) => setSettings({ agentFilter: id })}
            showAll
          />
        )}
      </PanelHeader>
      <ScrollArea className="flex-1">
        {visibleAutomations.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="ph-bold ph-heartbeat text-2xl mb-3 opacity-30" />
              <p className="text-sm">No routines{!isNovaSelected ? " for this agent" : " yet"}</p>
              {isNovaSelected && (
                <p className="text-xs text-text-disabled mt-1">
                  Ask Nova to set one up in chat
                </p>
              )}
            </div>
          </div>
        ) : (
          <div className="flex flex-col">
            {visibleUser.length > 0 && (
              <>
                <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">
                  User
                </div>
                {visibleUser.map((a) => (
                  <Fragment key={a.name}>{renderRow(a)}</Fragment>
                ))}
              </>
            )}
            {visibleSystem.length > 0 && (
              <>
                <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">
                  System
                </div>
                {visibleSystem.map((a) => (
                  <Fragment key={a.name}>{renderRow(a, true)}</Fragment>
                ))}
              </>
            )}
          </div>
        )}
      </ScrollArea>
    </>
  )

  const detail = selected ? (
    <AutomationDetail
      automation={selected}
      onDelete={() => handleDelete(selected)}
      onTrigger={() => handleTrigger(selected)}
      triggering={triggering === selected.name}
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
      onMobileTabChange={(tab) => {
        setMobileTab(tab)
        if (tab === 0) navigate("/apps/nova/pulse")
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
