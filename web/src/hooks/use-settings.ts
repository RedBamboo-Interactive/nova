import { useState, useCallback, useEffect } from "react"
import { api } from "../lib/api"

export interface NovaSettings {
  general: {
    port: number
  }
  docker: {
    enabled: boolean
    image?: string | null
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
    const data = await api.get<NovaSettings>("/api/apps/nova/settings")
    setSettings(data)
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  const updateDocker = useCallback(
    async (image: string | null) => {
      setSaving(true)
      try {
        await api.put("/api/apps/nova/settings/docker", { image })
        await refresh()
      } finally {
        setSaving(false)
      }
    },
    [refresh],
  )

  return { settings, saving, refresh, updateDocker }
}
