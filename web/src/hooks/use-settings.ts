import { useState, useCallback, useEffect } from "react"
import { api } from "@/lib/api"

export interface NovaSettings {
  identity: string
  general: {
    port: number
    redComputeUrl: string
  }
  tunnel: {
    enabled: boolean
    hostname?: string
    hasToken: boolean
  }
}

export function useSettings() {
  const [settings, setSettings] = useState<NovaSettings | null>(null)
  const [saving, setSaving] = useState(false)

  const refresh = useCallback(async () => {
    const data = await api.get<NovaSettings>("/api/settings")
    setSettings(data)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const updateIdentity = useCallback(
    async (content: string) => {
      setSaving(true)
      try {
        await api.put("/api/settings/identity", { content })
        await refresh()
      } finally {
        setSaving(false)
      }
    },
    [refresh],
  )

  const updateGeneral = useCallback(
    async (updates: Partial<NovaSettings["general"]>) => {
      setSaving(true)
      try {
        await api.put("/api/settings/general", updates)
        await refresh()
      } finally {
        setSaving(false)
      }
    },
    [refresh],
  )

  return { settings, saving, refresh, updateIdentity, updateGeneral }
}
