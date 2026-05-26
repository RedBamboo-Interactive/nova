import { useState, useEffect, useCallback, Fragment } from "react"
import { MasterDetailLayout, PanelHeader, ScrollArea, ItemListRow, Badge } from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { api } from "@/lib/api"

export function MemoryPanel() {
  const [files, setFiles] = useState<string[]>([])
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [content, setContent] = useState("")
  const [mobileTab, setMobileTab] = useState(0)

  const refreshManifest = useCallback(async () => {
    const data = await api.get<{ files: string[] }>("/api/memory/manifest")
    setFiles(data.files)
  }, [])

  useEffect(() => {
    refreshManifest()
  }, [refreshManifest])

  const loadFile = useCallback(async (path: string) => {
    setSelectedFile(path)
    setMobileTab(1)
    const data = await api.get<{ content: string }>(
      `/api/memory/file?path=${encodeURIComponent(path)}`,
    )
    setContent(data.content)
  }, [])

  const grouped = files.reduce<Record<string, string[]>>((acc, file) => {
    const dir = file.split("/").slice(0, -1).join("/") || "root"
    ;(acc[dir] ??= []).push(file)
    return acc
  }, {})

  const sidebar = (
    <>
      <PanelHeader title="Journal" />
      <ScrollArea className="flex-1">
        {files.length === 0 ? (
          <div className="flex items-center justify-center py-12 text-text-muted">
            <div className="text-center">
              <i className="fa-solid fa-book text-2xl mb-3 opacity-30" />
              <p className="text-sm">No journal entries yet</p>
              <p className="text-xs text-text-disabled mt-1">
                Nova will create them as you chat
              </p>
            </div>
          </div>
        ) : (
          <div className="flex flex-col">
            {Object.entries(grouped).map(([dir, dirFiles]) => (
              <Fragment key={dir}>
                <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider px-4 pt-3 pb-1">
                  {dir}
                </div>
                {dirFiles.map((file) => {
                  const name = file.split("/").pop() ?? file
                  return (
                    <ItemListRow
                      key={file}
                      selected={selectedFile === file}
                      onClick={() => loadFile(file)}
                      icon={
                        <i className="fa-solid fa-file-lines text-xs text-text-muted" />
                      }
                      title={name}
                    />
                  )
                })}
              </Fragment>
            ))}
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
        if (tab === 0) setSelectedFile(null)
      }}
      sidebar={sidebar}
      detail={detail}
    />
  )
}
