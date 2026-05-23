import { useLocalStore } from "@redbamboo/utility"
import { settingsStore } from "@/lib/settings-store"
import type { LocalSettings } from "@/lib/settings-store"

export type { LocalSettings }

export function useLocalSettings(): LocalSettings {
  return useLocalStore(settingsStore)
}
