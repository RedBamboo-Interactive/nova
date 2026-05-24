import { useState, useEffect, useCallback } from "react"
import {
  Button,
  ItemList,
  ItemListRow,
  Badge,
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@redbamboo/ui"
import { api } from "@/lib/api"

interface Automation {
  name: string
  description: string
  schedule: string
  enabled: boolean
  removeOnTrigger: boolean
  actionType: string
  actionConfig?: Record<string, unknown>
  reportToDiscussionId?: string
  lastRun?: string
  nextRun?: string
  lastResult?: { triggered: boolean; summary: string }
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

function cronFiveToHuman([min, hour, dom, mon, dow]: [string, string, string, string, string]): string {
  if (min.startsWith("*/")) return `Every ${min.slice(2)} min`
  if (hour.startsWith("*/")) return `Every ${hour.slice(2)}h`
  if (min === "0" && hour === "*") return "Every hour"
  if (dom === "*" && mon === "*" && dow === "*") {
    if (hour !== "*" && min !== "*") {
      const h = parseInt(hour)
      const m = parseInt(min)
      const period = h >= 12 ? "PM" : "AM"
      const h12 = h === 0 ? 12 : h > 12 ? h - 12 : h
      return `Daily at ${h12}:${m.toString().padStart(2, "0")} ${period}`
    }
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

function AutomationDetail({ automation, onClose }: { automation: Automation; onClose: () => void }) {
  const meta = actionMeta[automation.actionType] ?? { icon: "fa-solid fa-gear", label: automation.actionType }

  return (
    <Dialog open onOpenChange={() => onClose()}>
      <DialogContent className="max-w-md sm:max-w-lg max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle className="text-lg flex items-center gap-2">
            <i className={`${meta.icon} text-sm text-primary`} />
            {automation.name}
          </DialogTitle>
        </DialogHeader>

        <div className="space-y-4 text-sm">
          {automation.description && (
            <p className="text-text-muted">{automation.description}</p>
          )}

          <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-xs">
            <span className="text-text-muted">Type</span>
            <span>{meta.label}</span>

            <span className="text-text-muted">Schedule</span>
            <span>{cronToHuman(automation.schedule)} <span className="text-text-muted">({automation.schedule})</span></span>

            <span className="text-text-muted">Status</span>
            <span className="flex items-center gap-2">
              {automation.enabled ? (
                <Badge variant="default">Active</Badge>
              ) : (
                <Badge variant="secondary">Disabled</Badge>
              )}
              {automation.removeOnTrigger && <Badge variant="secondary">One-shot</Badge>}
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
              <pre className="text-xs bg-overlay-5 rounded-md p-3 overflow-x-auto whitespace-pre-wrap break-all">
                {JSON.stringify(automation.actionConfig, null, 2)}
              </pre>
            </div>
          )}

          {automation.lastResult && (
            <div>
              <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-2">
                Last Result
              </div>
              <div className="text-xs bg-overlay-5 rounded-md p-3 flex items-center gap-2">
                <i className={`fa-solid ${automation.lastResult.triggered ? "fa-circle-check text-green-500" : "fa-circle-xmark text-text-muted"} text-xs`} />
                {automation.lastResult.summary}
              </div>
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

export function AutomationsPanel() {
  const [automations, setAutomations] = useState<Automation[]>([])
  const [selected, setSelected] = useState<Automation | null>(null)

  const refresh = useCallback(async () => {
    const data = await api.get<{ automations: Automation[] }>("/api/automations")
    setAutomations(data.automations)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const userAutomations = automations.filter((a) => !a.name.startsWith("system:"))
  const systemAutomations = automations.filter((a) => a.name.startsWith("system:"))

  const renderItem = (a: Automation, isSystem = false) => {
    const meta = actionMeta[a.actionType] ?? { icon: "fa-solid fa-gear", label: a.actionType }
    return (
      <ItemListRow
        icon={
          <i className={`${meta.icon} text-xs ${isSystem ? "text-text-muted" : a.enabled ? "text-primary" : "text-text-disabled"}`} />
        }
        title={isSystem ? a.name.replace("system:", "") : a.name}
        subtitle={
          [a.description, cronToHuman(a.schedule)].filter(Boolean).join(" — ")
        }
        badge={
          !isSystem ? (
            <>
              <Badge variant="outline">{meta.label}</Badge>
              {a.removeOnTrigger && <Badge variant="secondary">once</Badge>}
            </>
          ) : undefined
        }
        onClick={() => setSelected(a)}
        trailing={
          !isSystem ? (
            <Button
              variant="ghost"
              size="icon-xs"
              onClick={async (e) => {
                e.stopPropagation()
                await api.delete(`/api/automations/${a.name}`)
                refresh()
              }}
            >
              <i className="fa-solid fa-xmark" />
            </Button>
          ) : undefined
        }
      />
    )
  }

  return (
    <div className="h-full overflow-y-auto">
      <div className="max-w-2xl mx-auto p-6 space-y-8">
        <section>
          <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3">
            <i className="fa-solid fa-bolt mr-1.5" />
            Automations
          </div>
          <p className="text-xs text-muted-a60 mb-3 leading-relaxed">
            Background tasks Nova manages on her own. AI sessions, watchers,
            scheduled checks. Ask her to set one up in chat.
          </p>
          {userAutomations.length === 0 ? (
            <div className="flex-1 flex items-center justify-center py-12 text-text-muted">
              <div className="text-center">
                <i className="fa-solid fa-bolt text-2xl mb-3 opacity-30" />
                <p className="text-sm">No active automations</p>
                <p className="text-xs text-muted-a60 mt-1">
                  Ask Nova to watch something or schedule a task
                </p>
              </div>
            </div>
          ) : (
            <ItemList
              items={userAutomations}
              keyFn={(a) => a.name}
              renderItem={(a) => renderItem(a)}
            />
          )}
        </section>

        {systemAutomations.length > 0 && (
          <section>
            <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3">
              <i className="fa-solid fa-gear mr-1.5" />
              System
            </div>
            <ItemList
              items={systemAutomations}
              keyFn={(a) => a.name}
              renderItem={(a) => renderItem(a, true)}
            />
          </section>
        )}
      </div>

      {selected && (
        <AutomationDetail
          automation={selected}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}
