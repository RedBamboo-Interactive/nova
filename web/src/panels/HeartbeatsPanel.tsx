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
import { api } from "@/lib/api"

interface Automation {
  name: string
  description: string
  schedule: string
  enabled: boolean
  removeOnTrigger: boolean
  icon?: string
  actionType: string
  actionConfig?: Record<string, unknown>
  reportToDiscussionId?: string
  lastRun?: string
  nextRun?: string
  lastResult?: { triggered: boolean; summary: string; data?: string; sessionId?: string }
}

const actionMeta: Record<string, { icon: string; label: string }> = {
  "ai-session":     { icon: "fa-solid fa-brain",          label: "AI Session" },
  "http-check":     { icon: "fa-solid fa-satellite-dish",  label: "HTTP Watcher" },
  "builtin:backup": { icon: "fa-solid fa-box-archive",    label: "System" },
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
  if (a.icon) return a.icon.startsWith("fa-") ? `fa-solid ${a.icon}` : a.icon
  return (actionMeta[a.actionType] ?? { icon: "fa-solid fa-gear" }).icon
}

function AutomationDetail({ automation, onDelete }: { automation: Automation; onDelete: () => void }) {
  const meta = actionMeta[automation.actionType] ?? { icon: "fa-solid fa-gear", label: automation.actionType }
  const isSystem = automation.name.startsWith("system:")

  return (
    <div className="h-full flex flex-col">
      <PanelHeader title={isSystem ? automation.name.replace("system:", "") : automation.name}>
        {!isSystem && (
          <Button
            variant="ghost"
            size="icon-xs"
            onClick={onDelete}
            title="Delete automation"
          >
            <i className="fa-solid fa-trash text-xs" />
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

        {automation.actionConfig && Object.keys(automation.actionConfig).length > 0 && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
              Configuration
            </div>
            <ConfigDisplay config={automation.actionConfig} />
          </div>
        )}

        {automation.lastResult && (
          <div>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
              Last Result
            </div>
            <div className="bg-overlay-5 rounded-md p-3 space-y-2">
              <div className="flex items-center gap-2 text-xs">
                <i className={`fa-solid ${automation.lastResult.triggered ? "fa-circle-check text-green-500" : "fa-circle-xmark text-text-muted"}`} />
                <span className="font-medium">
                  {automation.lastResult.triggered ? "Triggered" : "Not triggered"}
                </span>
                {automation.lastResult.sessionId && (
                  <a
                    href={`http://localhost:18800/ai-session/sessions/${automation.lastResult.sessionId}`}
                    target="_blank"
                    rel="noreferrer"
                    className="ml-auto text-text-muted hover:text-primary transition-colors"
                    title="View session"
                  >
                    <i className="fa-solid fa-arrow-up-right-from-square text-[10px]" />
                  </a>
                )}
              </div>
              <div className="text-sm leading-relaxed markdown-body">
                <MarkdownRenderer content={automation.lastResult.summary} />
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

  const selectedName = urlAutomationName ?? null

  useBreadcrumbLabel(
    urlAutomationName ? `/pulse/${urlAutomationName}` : undefined,
    urlAutomationName ?? undefined,
  )

  const refresh = useCallback(async () => {
    const data = await api.get<{ automations: Automation[] }>("/api/automations")
    setAutomations(data.automations)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const selected = automations.find((a) => a.name === selectedName) ?? null

  const userAutomations = automations.filter((a) => !a.name.startsWith("system:"))
  const systemAutomations = automations.filter((a) => a.name.startsWith("system:"))

  const handleDelete = useCallback(async (name: string) => {
    await api.delete(`/api/automations/${name}`)
    if (selectedName === name) navigate("/pulse")
    refresh()
  }, [selectedName, refresh, navigate])

  const handleSelect = useCallback((name: string) => {
    navigate(`/pulse/${encodeURIComponent(name)}`)
    setMobileTab(1)
  }, [navigate])

  const renderRow = (a: Automation, isSystem = false) => {
    const meta = actionMeta[a.actionType] ?? { icon: "fa-solid fa-gear", label: a.actionType }
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
                handleDelete(a.name)
              }}
            >
              <i className="fa-solid fa-xmark text-xs" />
            </Button>
          ) : undefined
        }
      />
    )
  }

  const sidebar = (
    <>
      <PanelHeader title="Pulse" />
      <ScrollArea className="flex-1">
        {automations.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="fa-solid fa-heart-pulse text-2xl mb-3 opacity-30" />
              <p className="text-sm">No routines yet</p>
              <p className="text-xs text-text-disabled mt-1">
                Ask Nova to set one up in chat
              </p>
            </div>
          </div>
        ) : (
          <div className="flex flex-col">
            {userAutomations.length > 0 && (
              <>
                <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">
                  User
                </div>
                {userAutomations.map((a) => (
                  <Fragment key={a.name}>{renderRow(a)}</Fragment>
                ))}
              </>
            )}
            {systemAutomations.length > 0 && (
              <>
                <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">
                  System
                </div>
                {systemAutomations.map((a) => (
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
      onDelete={() => handleDelete(selected.name)}
    />
  ) : (
    <div className="h-full flex items-center justify-center text-text-muted">
      <div className="text-center">
        <i className="fa-solid fa-heart-pulse text-2xl mb-3 opacity-30" />
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
        if (tab === 0) navigate("/pulse")
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
