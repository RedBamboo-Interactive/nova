import { useState, useEffect, useCallback } from "react"
import { Button, ItemList, ItemListRow, Badge } from "@redbamboo/ui"
import { api } from "@/lib/api"

interface Heartbeat {
  name: string
  description: string
  intervalMinutes: number
  lastRun?: string
}

interface ScheduledTask {
  name: string
  description: string
  nextRun: string
  lastRun?: string
  recurring: boolean
  enabled: boolean
}

export function HeartbeatsPanel() {
  const [heartbeats, setHeartbeats] = useState<Heartbeat[]>([])
  const [tasks, setTasks] = useState<ScheduledTask[]>([])

  const refresh = useCallback(async () => {
    const [hb, sched] = await Promise.all([
      api.get<{ heartbeats: Heartbeat[] }>("/api/heartbeats"),
      api.get<{ tasks: ScheduledTask[] }>("/api/schedule"),
    ])
    setHeartbeats(hb.heartbeats)
    setTasks(sched.tasks)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  return (
    <div className="h-full overflow-y-auto">
      <div className="max-w-2xl mx-auto p-6 space-y-8">
        {/* Heartbeats */}
        <section>
          <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3">
            <i className="fa-solid fa-heart mr-1.5" />
            Heartbeats
          </div>
          <p className="text-xs text-muted-a60 mb-3 leading-relaxed">
            Recurring background loops Nova runs on her own. Ask her to watch
            something in chat and she'll set one up.
          </p>
          {heartbeats.length === 0 ? (
            <div className="flex-1 flex items-center justify-center py-12 text-text-muted">
              <div className="text-center">
                <i className="fa-solid fa-heart text-2xl mb-3 opacity-30" />
                <p className="text-sm">No active heartbeats</p>
                <p className="text-xs text-muted-a60 mt-1">
                  Ask Nova to set one up in chat
                </p>
              </div>
            </div>
          ) : (
            <ItemList
              items={heartbeats}
              keyFn={(hb) => hb.name}
              renderItem={(hb) => (
                <ItemListRow
                  icon={
                    <i className="fa-solid fa-heart text-xs text-primary" />
                  }
                  title={hb.name}
                  subtitle={`${hb.description} — every ${hb.intervalMinutes}m`}
                  trailing={
                    <Button
                      variant="ghost"
                      size="icon-xs"
                      onClick={async () => {
                        await api.delete(`/api/heartbeats/${hb.name}`)
                        refresh()
                      }}
                    >
                      <i className="fa-solid fa-xmark" />
                    </Button>
                  }
                />
              )}
            />
          )}
        </section>

        {/* Scheduled Tasks */}
        <section>
          <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3">
            <i className="fa-solid fa-clock mr-1.5" />
            Scheduled Tasks
          </div>
          <p className="text-xs text-muted-a60 mb-3 leading-relaxed">
            One-time or recurring tasks Nova has scheduled. Reminders, checks,
            or deferred actions.
          </p>
          {tasks.length === 0 ? (
            <div className="flex-1 flex items-center justify-center py-12 text-text-muted">
              <div className="text-center">
                <i className="fa-solid fa-clock text-2xl mb-3 opacity-30" />
                <p className="text-sm">No scheduled tasks</p>
                <p className="text-xs text-muted-a60 mt-1">
                  Ask Nova to schedule something in chat
                </p>
              </div>
            </div>
          ) : (
            <ItemList
              items={tasks}
              keyFn={(t) => t.name}
              renderItem={(task) => (
                <ItemListRow
                  icon={
                    <i
                      className={`fa-solid fa-clock text-xs ${task.enabled ? "text-primary" : "text-text-disabled"}`}
                    />
                  }
                  title={task.name}
                  subtitle={task.description}
                  badge={
                    !task.enabled ? (
                      <Badge variant="secondary">done</Badge>
                    ) : task.recurring ? (
                      <Badge variant="outline">recurring</Badge>
                    ) : undefined
                  }
                  trailing={
                    <Button
                      variant="ghost"
                      size="icon-xs"
                      onClick={async () => {
                        await api.delete(`/api/schedule/${task.name}`)
                        refresh()
                      }}
                    >
                      <i className="fa-solid fa-xmark" />
                    </Button>
                  }
                />
              )}
            />
          )}
        </section>
      </div>
    </div>
  )
}
