import { useState, useCallback, useEffect } from "react"
import { api } from "@/lib/api"

export interface NovaSettings {
  identity: string
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

  const updateDocker = useCallback(
    async (image: string | null) => {
      setSaving(true)
      try {
        await api.put("/api/settings/docker", { image })
        await refresh()
      } finally {
        setSaving(false)
      }
    },
    [refresh],
  )

  return { settings, saving, refresh, updateIdentity, updateDocker }
}
