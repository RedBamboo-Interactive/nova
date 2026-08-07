import { useState, useEffect, useCallback } from "react"
import { useParams, useNavigate } from "react-router-dom"
import { MasterDetailLayout, PanelHeader, ScrollArea, ItemListRow, Badge, Button, useToast } from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { useBreadcrumbLabel } from "@redbamboo/utility"
import { api } from "../lib/api"
import { useLocalSettings } from "../hooks/use-local-settings"
import { useAgents } from "../hooks/use-agents"
import { AgentPicker } from "../components/agent-picker"
import { setSettings } from "../lib/settings-store"

export function MemoryPanel() {
  const { "*": splatPath } = useParams()
  const navigate = useNavigate()
  const [files, setFiles] = useState<string[]>([])
  const [content, setContent] = useState("")
  const [mobileTab, setMobileTab] = useState(0)
  const [openFolders, setOpenFolders] = useState<Set<string>>(new Set())
  const { agents, defaultAgentId } = useAgents()
  const { toast } = useToast()
  const settings = useLocalSettings()
  const agentFilter = settings.agentFilter
  const multiAgent = agents.length > 1
  const selectedAgent = agents.find((agent) => agent.id === agentFilter)
    ?? agents.find((agent) => agent.id === defaultAgentId)
    ?? agents[0]
  const selectedAgentId = selectedAgent?.id ?? null

  // On the index route the host's /apps/nova/* splat ("journal") leaks through
  // the merged params — only a deeper path is an actual file selection.
  const selectedFile = splatPath && splatPath !== "journal" ? splatPath : null

  useBreadcrumbLabel(
    selectedFile ? `/apps/nova/journal/${selectedFile}` : undefined,
    selectedFile?.split("/").pop(),
  )

  const agentParam = selectedAgentId ? `&agent=${encodeURIComponent(selectedAgentId)}` : ""

  const refreshManifest = useCallback(async () => {
    if (!selectedAgentId) {
      setFiles([])
      return
    }
    const url = `/api/apps/nova/workspace/manifest?agent=${encodeURIComponent(selectedAgentId)}`
    const data = await api.get<{ files: string[] }>(url)
    setFiles(data.files)
  }, [selectedAgentId])

  useEffect(() => {
    refreshManifest()
  }, [refreshManifest])

  useEffect(() => {
    if (!selectedFile || !selectedAgentId) return
    api.get<{ content: string }>(
      `/api/apps/nova/memory/file?path=${encodeURIComponent(selectedFile)}${agentParam}`,
    ).then((data) => setContent(data.content))
  }, [selectedFile, selectedAgentId, agentParam])

  const handleSelectFile = useCallback((path: string) => {
    navigate(`/apps/nova/journal/${path}`)
    setMobileTab(1)
  }, [navigate])

  const handleRevealWorkspace = useCallback(async () => {
    if (!selectedAgentId) return
    const query = `?agent=${encodeURIComponent(selectedAgentId)}`
    try {
      await api.post(`/api/apps/nova/workspace/reveal${query}`)
    } catch (error) {
      toast({
        variant: "error",
        title: "Could not open workspace",
        description: error instanceof Error ? error.message : "Unknown error",
      })
    }
  }, [selectedAgentId, toast])

  const handleSelectAgent = useCallback((id: string | null) => {
    if (!id) return
    setSettings({ agentFilter: id })
    navigate("/apps/nova/journal")
    setContent("")
    setMobileTab(0)
  }, [navigate])

  const handleOpenEntityWorkspace = useCallback(() => {
    if (selectedAgent?.workspaceId) navigate(`/workspace/${selectedAgent.workspaceId}`)
  }, [navigate, selectedAgent?.workspaceId])

  const grouped = files.reduce<Record<string, string[]>>((acc, file) => {
    const dir = file.split("/").slice(0, -1).join("/") || "root"
    ;(acc[dir] ??= []).push(file)
    return acc
  }, {})

  const sidebar = (
    <>
      <PanelHeader title="Journal">
        {multiAgent && (
          <AgentPicker
            agents={agents}
            selectedId={selectedAgentId}
            onSelect={handleSelectAgent}
          />
        )}
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={() => void handleRevealWorkspace()}
          disabled={!selectedAgentId}
          title="Open workspace folder in Windows Explorer"
          aria-label="Open workspace folder in Windows Explorer"
        >
          <i className="ph-bold ph-folder-open text-xs" />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={handleOpenEntityWorkspace}
          disabled={!selectedAgent?.workspaceId}
          title="Entity-backed VFS — open in Workspace"
          aria-label="Entity-backed VFS — open in Workspace"
        >
          <i className="ph-bold ph-database text-xs" />
        </Button>
      </PanelHeader>
      <ScrollArea className="flex-1">
        {files.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="ph-bold ph-book text-2xl mb-3 opacity-30" />
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
                    <i className={`ph-bold ph-caret-right text-[9px] text-text-disabled transition-transform duration-150 ${isOpen ? "rotate-90" : ""}`} />
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
                          <i className="ph-bold ph-file-text text-xs text-text-muted" />
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
        <i className="ph-bold ph-file-text text-2xl mb-3 opacity-30" />
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
        if (tab === 0) navigate("/apps/nova/journal")
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
