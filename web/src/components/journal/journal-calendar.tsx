import { useEffect, useMemo, useState } from "react"
import { Button, Popover, PopoverContent, PopoverTrigger, buttonVariants, cn } from "@redbamboo/ui"
import { fetchJournalMonth } from "../../lib/journal-api"
import {
  CALENDAR_WEEKDAYS,
  addMonths,
  calendarWeeks,
  journalDayLabel,
  monthLabel,
  monthOf,
  type CalendarCell,
} from "../../lib/journal"
import type { JournalCalendarDay } from "../../lib/journal-types"

interface JournalCalendarProps {
  agent: string | null
  day: string
  today: string
  onSelect: (day: string) => void
}

const MARKS = [
  { key: "own", label: "On my own", color: "bg-primary" },
  { key: "withYou", label: "With you", color: "bg-accent-teal" },
  { key: "failed", label: "Automation failed", color: "bg-destructive" },
] as const

function cellLabel(cell: CalendarCell): string {
  const marks = MARKS.filter((mark) => cell.marks?.[mark.key]).map((mark) => mark.label.toLowerCase())
  return [journalDayLabel(cell.day), ...marks].join(", ")
}

/**
 * The day bar's date button and its month. `@redbamboo/ui` has no calendar, so this is a `Popover`
 * of its `Button`s; the dots are the same tokens as the journal's own rail.
 */
export function JournalCalendar({ agent, day, today, onSelect }: JournalCalendarProps) {
  const [open, setOpen] = useState(false)
  const [month, setMonth] = useState(() => monthOf(day))
  const [known, setKnown] = useState<ReadonlyMap<string, readonly JournalCalendarDay[]>>(new Map())

  useEffect(() => { if (open) setMonth(monthOf(day)) }, [day, open])
  useEffect(() => { setKnown(new Map()) }, [agent])

  const days = useMemo(() => [...known.values()].flat(), [known])
  const weeks = useMemo(() => calendarWeeks(month, today, day, days), [month, today, day, days])

  // The month, and whichever past month shares a week with it, each read once while open.
  useEffect(() => {
    if (!open) return
    const wanted = new Set(weeks.flat().filter((cell) => !cell.future).map((cell) => monthOf(cell.day)))
    let live = true
    for (const each of wanted) {
      if (known.has(each)) continue
      void fetchJournalMonth(agent, each).then(
        (read) => { if (live) setKnown((previous) => new Map(previous).set(each, read.days)) },
        () => { if (live) setKnown((previous) => new Map(previous).set(each, [])) },
      )
    }
    return () => { live = false }
  }, [agent, known, open, weeks])

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        className={cn(buttonVariants({ variant: "outline", size: "default" }), "min-w-0 text-[13px]")}
        aria-label={`${journalDayLabel(day)}, choose a day`}
      >
        <i aria-hidden="true" className="ph-bold ph-calendar-blank text-text-muted" />
        <span className="truncate font-medium text-contrast">{journalDayLabel(day)}</span>
        {day === today && <span className="text-[11px] font-medium text-accent-teal">Today</span>}
      </PopoverTrigger>
      <PopoverContent align="start" className="w-[19rem] gap-2 p-3" data-slot="journal-calendar">
        <div className="flex items-center justify-between">
          <Button variant="ghost" size="icon" aria-label="Previous month" onClick={() => setMonth(addMonths(month, -1))}>
            <i aria-hidden="true" className="ph-bold ph-caret-left" />
          </Button>
          <span className="text-[13px] font-semibold text-contrast">{monthLabel(month)}</span>
          <Button
            variant="ghost"
            size="icon"
            aria-label="Next month"
            disabled={addMonths(month, 1) > monthOf(today)}
            onClick={() => setMonth(addMonths(month, 1))}
          >
            <i aria-hidden="true" className="ph-bold ph-caret-right" />
          </Button>
        </div>
        <div role="grid" aria-label={monthLabel(month)} className="flex flex-col gap-1">
          <div role="row" className="grid grid-cols-7 gap-1">
            {CALENDAR_WEEKDAYS.map((weekday, index) => (
              <span key={index} role="columnheader" className="text-center text-[11px] font-medium text-text-muted">{weekday}</span>
            ))}
          </div>
          {weeks.map((week) => (
            <div key={week[0].day} role="row" className="grid grid-cols-7 gap-1">
              {week.map((cell) => (
                <span key={cell.day} role="gridcell">
                  <Button
                    variant="ghost"
                    disabled={cell.future}
                    aria-current={cell.today ? "date" : undefined}
                    aria-pressed={cell.selected}
                    aria-label={cellLabel(cell)}
                    onClick={() => { setOpen(false); onSelect(cell.day) }}
                    className={cn(
                      "h-10 w-full flex-col gap-0.5 border px-0 text-[13px]",
                      cell.marks && "bg-overlay-6",
                      cell.selected && "bg-overlay-12 font-semibold text-contrast",
                      cell.today ? "border-primary" : "border-transparent",
                      !cell.inMonth && "text-text-muted",
                    )}
                  >
                    {cell.label}
                    <span aria-hidden="true" className="flex h-1 items-center gap-0.5">
                      {MARKS.filter((mark) => cell.marks?.[mark.key]).map((mark) => (
                        <span key={mark.key} className={cn("size-1 rounded-full", mark.color)} />
                      ))}
                    </span>
                  </Button>
                </span>
              ))}
            </div>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 pt-1 text-[11px] text-text-muted">
          {MARKS.map((mark) => (
            <span key={mark.key} className="inline-flex items-center gap-1">
              <span aria-hidden="true" className={cn("size-1.5 rounded-full", mark.color)} />
              {mark.label}
            </span>
          ))}
        </div>
      </PopoverContent>
    </Popover>
  )
}
