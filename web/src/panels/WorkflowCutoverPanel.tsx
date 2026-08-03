import { useCallback, useEffect, useMemo, useState } from "react"
import { Badge, Button, PanelHeader, useToast } from "@redbamboo/ui"
import { api } from "../lib/api"

interface MigrationIssue {
  code: string
  message: string
}

interface MigrationPreview {
  automationId: string
  automationName: string
  actionType: string
  strategy: string
  blockers: MigrationIssue[]
  ready: boolean
  sourceFingerprint?: string
  rollbackAvailable: boolean
}

interface MigrationFleet {
  definitions: number
  ready: number
  blocked: number
  alreadyWorkflow: number
  items: MigrationPreview[]
}

interface MigrationResult {
  changed: boolean
  workflowId: string
  revisionId: string
  appliedFingerprint: string
}

interface RollbackResult {
  workflowId: string
  revisionId?: string
  appliedFingerprint: string
}

const DEFAULT_REASON = "Reviewed in Nova Pulse for workflow-only cutover"

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Unknown migration error"
}

export function WorkflowCutoverPanel({ onChanged }: { onChanged: () => Promise<void> }) {
  const { toast } = useToast()
  const [fleet, setFleet] = useState<MigrationFleet | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [reason, setReason] = useState(DEFAULT_REASON)
  const [busy, setBusy] = useState<string | null>(null)
  const [progress, setProgress] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setFleet(await api.get<MigrationFleet>("/api/automations/workflow-migrations"))
    } catch (error) {
      setFleet(null)
      setLoadError(errorMessage(error))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const migratable = useMemo(() => fleet?.items.filter(item =>
    item.ready && item.strategy !== "already-workflow" && item.sourceFingerprint) ?? [], [fleet])

  const refreshAll = useCallback(async () => {
    await Promise.all([load(), onChanged()])
  }, [load, onChanged])

  const migrate = useCallback(async (item: MigrationPreview) => {
    if (!item.sourceFingerprint || !reason.trim()) return
    setBusy(item.automationId)
    try {
      await api.post<MigrationResult>(`/api/automations/${item.automationId}/workflow-migration`, {
        expectedFingerprint: item.sourceFingerprint,
        reviewReason: reason.trim(),
      })
      toast({ variant: "success", title: `${item.automationName} migrated`, description: "The reviewed workflow revision is now pinned." })
      await refreshAll()
    } catch (error) {
      toast({ variant: "error", title: "Migration failed", description: errorMessage(error) })
    } finally {
      setBusy(null)
    }
  }, [reason, refreshAll, toast])

  const rollback = useCallback(async (item: MigrationPreview) => {
    if (!item.sourceFingerprint || !reason.trim()) return
    if (!window.confirm(`Roll back ${item.automationName} to its preserved legacy action?`)) return
    setBusy(item.automationId)
    try {
      await api.post<RollbackResult>(`/api/automations/${item.automationId}/workflow-migration/rollback`, {
        expectedFingerprint: item.sourceFingerprint,
        reason: reason.trim(),
      })
      toast({ variant: "success", title: `${item.automationName} rolled back`, description: "The reviewed workflow and revision remain preserved for audit." })
      await refreshAll()
    } catch (error) {
      toast({ variant: "error", title: "Rollback failed", description: errorMessage(error) })
    } finally {
      setBusy(null)
    }
  }, [reason, refreshAll, toast])

  const rehearse = useCallback(async (item: MigrationPreview) => {
    if (!item.sourceFingerprint || !reason.trim()) return
    if (!window.confirm(`Run apply -> rollback -> reapply -> rollback for ${item.automationName}? It will finish on the legacy definition.`)) return
    setBusy(item.automationId)
    try {
      setProgress(`Applying ${item.automationName}`)
      const first = await api.post<MigrationResult>(`/api/automations/${item.automationId}/workflow-migration`, {
        expectedFingerprint: item.sourceFingerprint,
        reviewReason: `${reason.trim()} (rehearsal apply 1)`,
      })
      setProgress(`Rolling back ${item.automationName}`)
      const firstRollback = await api.post<RollbackResult>(`/api/automations/${item.automationId}/workflow-migration/rollback`, {
        expectedFingerprint: first.appliedFingerprint,
        reason: `${reason.trim()} (rehearsal rollback 1)`,
      })
      setProgress(`Reapplying ${item.automationName}`)
      const second = await api.post<MigrationResult>(`/api/automations/${item.automationId}/workflow-migration`, {
        expectedFingerprint: firstRollback.appliedFingerprint,
        reviewReason: `${reason.trim()} (rehearsal apply 2)`,
      })
      if (second.workflowId !== first.workflowId || second.revisionId !== first.revisionId) {
        throw new Error("Reapply did not reuse the preserved workflow and revision lineage")
      }
      setProgress(`Restoring ${item.automationName} to legacy`)
      await api.post<RollbackResult>(`/api/automations/${item.automationId}/workflow-migration/rollback`, {
        expectedFingerprint: second.appliedFingerprint,
        reason: `${reason.trim()} (rehearsal rollback 2)`,
      })
      toast({ variant: "success", title: "Rollback rehearsal passed", description: `${item.automationName} reused the same workflow and revision, then returned to legacy.` })
      await refreshAll()
    } catch (error) {
      let description = errorMessage(error)
      try {
        const current = await api.get<MigrationPreview>(`/api/automations/${item.automationId}/workflow-migration`)
        if (current.strategy === "already-workflow" && current.rollbackAvailable && current.sourceFingerprint) {
          await api.post<RollbackResult>(`/api/automations/${item.automationId}/workflow-migration/rollback`, {
            expectedFingerprint: current.sourceFingerprint,
            reason: `${reason.trim()} (automatic rehearsal recovery)`,
          })
          description += ". The automation was restored to its legacy definition."
        }
      } catch (recoveryError) {
        description += `. Automatic rollback also failed: ${errorMessage(recoveryError)}`
      }
      toast({ variant: "error", title: "Rollback rehearsal stopped", description })
      await refreshAll()
    } finally {
      setProgress(null)
      setBusy(null)
    }
  }, [reason, refreshAll, toast])

  const migrateAll = useCallback(async () => {
    if (!reason.trim() || migratable.length === 0) return
    if (!window.confirm(`Review and migrate ${migratable.length} ready automation${migratable.length === 1 ? "" : "s"}? Blocked definitions will not be changed.`)) return
    setBusy("all")
    const failures: string[] = []
    for (let index = 0; index < migratable.length; index++) {
      const item = migratable[index]!
      setProgress(`Migrating ${index + 1}/${migratable.length}: ${item.automationName}`)
      try {
        await api.post<MigrationResult>(`/api/automations/${item.automationId}/workflow-migration`, {
          expectedFingerprint: item.sourceFingerprint,
          reviewReason: reason.trim(),
        })
      } catch (error) {
        failures.push(`${item.automationName}: ${errorMessage(error)}`)
      }
    }
    await refreshAll()
    setProgress(null)
    setBusy(null)
    if (failures.length === 0) {
      toast({ variant: "success", title: "Ready automations migrated", description: `${migratable.length} reviewed workflow revisions are now pinned.` })
    } else {
      toast({ variant: "error", title: `${failures.length} migration${failures.length === 1 ? "" : "s"} failed`, description: failures.join(" | ") })
    }
  }, [migratable, reason, refreshAll, toast])

  return (
    <div className="h-full flex flex-col">
      <PanelHeader title="Workflow cutover">
        <Button variant="ghost" size="xs" onClick={() => void load()} disabled={loading || !!busy}>
          <i className={`ph-bold ${loading ? "ph-spinner animate-spin" : "ph-arrows-clockwise"} text-xs mr-1`} />
          Refresh
        </Button>
      </PanelHeader>
      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        <div>
          <div className="text-sm font-medium">Review visual workflows before enforcement</div>
          <p className="text-xs text-text-muted mt-1 leading-relaxed">
            Each migration pins an immutable reviewed revision and keeps the legacy action as rollback material. Blocked definitions remain untouched.
          </p>
        </div>

        {loadError ? (
          <div className="rounded-md border border-red-500/30 bg-red-500/5 p-3 text-xs text-text-muted">{loadError}</div>
        ) : loading && !fleet ? (
          <div className="rounded-md bg-overlay-5 p-3 text-xs text-text-muted">Loading migration readiness...</div>
        ) : fleet ? <>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
            {[
              ["Definitions", fleet.definitions],
              ["Ready", fleet.ready],
              ["Blocked", fleet.blocked],
              ["Migrated", fleet.alreadyWorkflow],
            ].map(([label, value]) => (
              <div key={label} className="rounded-md border border-overlay-10 bg-overlay-5 p-3">
                <div className="text-[10px] uppercase tracking-wider text-text-disabled">{label}</div>
                <div className="text-xl font-semibold mt-1">{value}</div>
              </div>
            ))}
          </div>

          <div className="space-y-2">
            <label className="text-[11px] font-medium text-text-muted uppercase tracking-wider" htmlFor="workflow-review-reason">Review reason</label>
            <textarea
              id="workflow-review-reason"
              value={reason}
              onChange={event => setReason(event.target.value)}
              rows={2}
              className="w-full resize-y rounded-md border border-overlay-10 bg-overlay-5 px-3 py-2 text-sm outline-none focus:border-primary"
            />
            <div className="flex items-center gap-2 flex-wrap">
              <Button size="sm" onClick={() => void migrateAll()} disabled={!!busy || migratable.length === 0 || !reason.trim()}>
                <i className={`ph-bold ${busy === "all" ? "ph-spinner animate-spin" : "ph-git-merge"} mr-1.5`} />
                Migrate {migratable.length} ready
              </Button>
              {progress && <span className="text-xs text-text-muted">{progress}</span>}
            </div>
          </div>

          <div className="rounded-md border border-overlay-10 divide-y divide-overlay-10 overflow-hidden">
            {fleet.items.map(item => {
              const itemBusy = busy === item.automationId
              return <div key={item.automationId} className="p-3 space-y-2">
                <div className="flex items-start gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="text-sm font-medium">{item.automationName}</div>
                    <div className="text-[11px] text-text-disabled mt-0.5">{item.strategy} · {item.actionType}</div>
                  </div>
                  {item.strategy === "already-workflow" ? <Badge variant="default">Migrated</Badge>
                    : item.ready ? <Badge variant="outline">Ready</Badge>
                      : <Badge variant="secondary">Blocked</Badge>}
                </div>
                {item.blockers.length > 0 && (
                  <div className="space-y-1">
                    {item.blockers.map(blocker => <div key={blocker.code} className="text-xs text-amber-600 dark:text-amber-400">
                      <span className="font-mono">{blocker.code}</span>: {blocker.message}
                    </div>)}
                  </div>
                )}
                <div className="flex items-center gap-2 flex-wrap">
                  <a className="text-xs text-primary hover:underline" href={`/apps/nova/pulse/${encodeURIComponent(item.automationId)}`}>Inspect</a>
                  {item.ready && item.strategy !== "already-workflow" && (
                    <>
                      <Button variant="ghost" size="xs" onClick={() => void migrate(item)} disabled={!!busy || !reason.trim()}>
                        {itemBusy ? "Migrating..." : "Migrate"}
                      </Button>
                      <Button variant="ghost" size="xs" onClick={() => void rehearse(item)} disabled={!!busy || !reason.trim()}>
                        Rehearse rollback
                      </Button>
                    </>
                  )}
                  {item.strategy === "already-workflow" && item.rollbackAvailable && (
                    <Button variant="ghost" size="xs" onClick={() => void rollback(item)} disabled={!!busy || !reason.trim()}>
                      {itemBusy ? "Rolling back..." : "Roll back"}
                    </Button>
                  )}
                </div>
              </div>
            })}
          </div>
        </> : null}
      </div>
    </div>
  )
}
