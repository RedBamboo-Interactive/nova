import { api } from "./api"
import type { JournalDay, JournalMonth } from "./journal-types"

function query(values: Record<string, string | null | undefined>): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(values)) if (value) params.set(key, value)
  const text = params.toString()
  return text ? `?${text}` : ""
}

export function fetchJournalDay(agent: string | null, day: string | null): Promise<JournalDay> {
  return api.get<JournalDay>(`/api/apps/nova/journal${query({ agent, day })}`)
}

export function fetchJournalMonth(agent: string | null, month: string): Promise<JournalMonth> {
  return api.get<JournalMonth>(`/api/apps/nova/journal/days${query({ agent, month })}`)
}
