import { useState, useEffect, useCallback } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { MasterDetailLayout, PanelHeader, ScrollArea, ItemListRow, Badge } from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { useBreadcrumbLabel } from "@redbamboo/utility"
import { api } from "@/lib/api"
import { useLocalSettings } from "@/hooks/use-local-settings"
import { useAgents } from "@/hooks/use-agents"
import { AgentPicker } from "@/components/agent-picker"
import { setSettings } from "@/lib/settings-store"

export function MemoryPanel() {
  const { "*": splatPath } = useParams()
  const navigate = useNavigate()
  const [files, setFiles] = useState<string[]>([])
  const [content, setContent] = useState("")
  const [mobileTab, setMobileTab] = useState(0)
  const [openFolders, setOpenFolders] = useState<Set<string>>(new Set())
  const { agents } = useAgents()
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter
  const multiAgent = agents.length > 1

  const selectedFile = splatPath || null

  useBreadcrumbLabel(
    selectedFile ? `/journal/${selectedFile}` : undefined,
    selectedFile?.split("/").pop(),
  )

  const agentParam = agentFilter ? `&agent=${encodeURIComponent(agentFilter)}` : ""

  const refreshManifest = useCallback(async () => {
    const url = agentFilter
      ? `/api/workspace/manifest?agent=${encodeURIComponent(agentFilter)}`
      : "/api/workspace/manifest"
    const data = await api.get<{ files: string[] }>(url)
    setFiles(data.files)
  }, [agentFilter])

  useEffect(() => {
    refreshManifest()
  }, [refreshManifest])

  useEffect(() => {
    if (!selectedFile) return
    api.get<{ content: string }>(
      `/api/memory/file?path=${encodeURIComponent(selectedFile)}${agentParam}`,
    ).then((data) => setContent(data.content))
  }, [selectedFile, agentParam])

  const handleSelectFile = useCallback((path: string) => {
    navigate(`/journal/${path}`)
    setMobileTab(1)
  }, [navigate])

  const grouped = files.reduce<Record<string, string[]>>((acc, file) => {
    const dir = file.split("/").slice(0, -1).join("/") || "root"
    ;(acc[dir] ??= []).push(file)
    return acc
  }, {})

  const sidebar = (
    <>
      <PanelHeader title={multiAgent ? undefined : "Journal"}>
        {multiAgent && (
          <AgentPicker
            agents={agents}
            selectedId={agentFilter}
            onSelect={(id) => setSettings({ agentFilter: id })}
            showAll
          />
        )}
      </PanelHeader>
      <ScrollArea className="flex-1">
        {files.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="fa-solid fa-book text-2xl mb-3 opacity-30" />
              <p className="text-sm">No files in workspace</p>
            </div>
          </div>
        ) : (
          <div className="flex flex-col">
            {Object.entries(grouped).map(([dir, dirFiles]) => {
              const isOpen = openFolders.has(dir)
              return (
                <div key={dir}>
                  <button
                    className="w-full flex items-center gap-1.5 px-4 pt-3 pb-1 text-left hover:text-text-muted transition-colors"
                    onClick={() => setOpenFolders(prev => {
                      const next = new Set(prev)
                      next.has(dir) ? next.delete(dir) : next.add(dir)
                      return next
                    })}
                  >
                    <i className={`fa-solid fa-chevron-right text-[9px] text-text-disabled transition-transform duration-150 ${isOpen ? "rotate-90" : ""}`} />
                    <span className="text-[10px] font-medium text-text-disabled uppercase tracking-wider">
                      {dir}
                    </span>
                  </button>
                  {isOpen && dirFiles.map((file) => {
                    const name = file.split("/").pop() ?? file
                    return (
                      <ItemListRow
                        key={file}
                        selected={selectedFile === file}
                        onClick={() => handleSelectFile(file)}
                        icon={
                          <i className="fa-solid fa-file-lines text-xs text-text-muted" />
                        }
                        title={name}
                      />
                    )
                  })}
                </div>
              )
            })}
          </div>
        )}
      </ScrollArea>
    </>
  )

  const detail = selectedFile ? (
    <div className="h-full flex flex-col">
      <PanelHeader title={selectedFile.split("/").pop() ?? selectedFile}>
        <Badge variant="outline">{selectedFile}</Badge>
      </PanelHeader>
      <div className="flex-1 overflow-y-auto p-4">
        <div className="text-sm leading-relaxed markdown-body">
          <MarkdownRenderer content={content} />
        </div>
      </div>
    </div>
  ) : (
    <div className="h-full flex items-center justify-center text-text-muted">
      <div className="text-center">
        <i className="fa-solid fa-file-lines text-2xl mb-3 opacity-30" />
        <p className="text-sm">Select a file to view</p>
      </div>
    </div>
  )

  return (
    <MasterDetailLayout
      layoutKey="nova-memory"
      mobileLabels={["Files", "Content"]}
      mobileTab={mobileTab}
      onMobileTabChange={(tab) => {
        setMobileTab(tab)
        if (tab === 0) navigate("/journal")
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
