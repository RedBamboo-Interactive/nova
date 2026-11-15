import { Switch } from "@redbamboo/ui"
import { useEventTypes } from "../hooks/use-event-types"
import { useLocalSettings } from "../hooks/use-local-settings"
import {
  activityMarkerRows,
  eventMarkerRows,
  normalizeHiddenFriezeMarkers,
  setFriezeMarkerShown,
  type FriezeMarkerScope,
  type FriezeMarkerSettingRow,
} from "../lib/frieze-marker-settings"
import { setSettings as setLocalSettings } from "../lib/settings-store"

const SCOPES: readonly { scope: FriezeMarkerScope; label: string }[] = [
  { scope: "live", label: "Live" },
  { scope: "discussions", label: "Discussions" },
]

const COLUMNS = "grid grid-cols-[minmax(0,1fr)_4.5rem_5.5rem] items-center gap-x-2"

/**
 * One row per marker kind with a switch for Live and one for every other
 * discussion. Event rows come from the registered event types, so a new type
 * appears here by itself. Hiding is display only.
 */
export function FriezeMarkerSettings() {
  const { hiddenFriezeMarkers } = useLocalSettings()
  const hidden = normalizeHiddenFriezeMarkers(hiddenFriezeMarkers)
  const { types } = useEventTypes()

  const setShown = (scope: FriezeMarkerScope, key: string, shown: boolean) =>
    setLocalSettings({ hiddenFriezeMarkers: setFriezeMarkerShown(hidden, scope, key, shown) })

  const markerRow = (row: FriezeMarkerSettingRow) => (
    <div key={row.key} data-slot="frieze-marker-setting" data-marker-key={row.key} className={`${COLUMNS} py-1.5`}>
      <span className="flex min-w-0 items-center gap-2 text-sm text-text-muted">
        <span
          aria-hidden="true"
          className={`size-2.5 shrink-0 ${row.shape === "dot" ? "rounded-full" : "rounded-[2px]"}`}
          style={{ backgroundColor: row.color ?? "var(--color-text-disabled)" }}
        />
        <span className="truncate">{row.name}</span>
      </span>
      {SCOPES.map(({ scope, label }) => (
        <span key={scope} className="flex justify-center">
          <Switch
            size="sm"
            checked={!hidden[scope].includes(row.key)}
            onCheckedChange={(shown) => setShown(scope, row.key, shown)}
            aria-label={`${row.name} in ${label}`}
            data-marker-scope={scope}
          />
        </span>
      ))}
    </div>
  )

  const group = (title: string, rows: FriezeMarkerSettingRow[]) => (
    <div className="pt-2">
      <div className={`${COLUMNS} pb-1 text-[11px] font-medium text-text-disabled`}>
        <span>{title}</span>
        {SCOPES.map(({ scope, label }) => <span key={scope} className="text-center">{label}</span>)}
      </div>
      {rows.map(markerRow)}
    </div>
  )

  return (
    <div data-setting-path="nova.timelineMarkers" className="py-2.5">
      <span className="text-sm text-text-muted">Timeline markers</span>
      <p className="text-xs text-muted-a60 mt-1 leading-relaxed">
        The dots and squares beside a reply. Hiding a kind only changes what you see: the events still arrive and stay in the conversation. A marker that needs you, such as a failed delivery or a pending approval, always shows.
      </p>
      {group("Events", eventMarkerRows(types))}
      {group("Activity", activityMarkerRows())}
    </div>
  )
}
