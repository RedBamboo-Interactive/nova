import { useState, useEffect, useCallback } from "react"
import { MasterDetailLayout, ItemList, ItemListRow, Badge } from "@redbamboo/ui"
import { MarkdownRenderer } from "@redbamboo/chat"
import { api } from "@/lib/api"

export function MemoryPanel() {
  const [files, setFiles] = useState<string[]>([])
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [content, setContent] = useState("")

  const refreshManifest = useCallback(async () => {
    const data = await api.get<{ files: string[] }>("/api/memory/manifest")
    setFiles(data.files)
  }, [])

  useEffect(() => {
    refreshManifest()
  }, [refreshManifest])

  const loadFile = useCallback(async (path: string) => {
    setSelectedFile(path)
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
    <div className="h-full overflow-y-auto p-3">
      <div className="text-[11px] font-medium text-text-muted uppercase tracking-wider mb-3 px-1">
        <i className="fa-solid fa-brain mr-1.5" />
        Memory
      </div>
      {files.length === 0 ? (
        <p className="text-xs text-text-muted px-1">
          No memory files yet. Nova will create them as you chat.
        </p>
      ) : (
        Object.entries(grouped).map(([dir, dirFiles]) => (
          <div key={dir} className="mb-3">
            <div className="text-[10px] font-medium text-text-disabled uppercase tracking-wider mb-1 px-1">
              {dir}
            </div>
            <ItemList
              items={dirFiles}
              keyFn={(f) => f}
              renderItem={(file) => {
                const name = file.split("/").pop() ?? file
                return (
                  <ItemListRow
                    selected={selectedFile === file}
                    onClick={() => loadFile(file)}
                    icon={
                      <i className="fa-solid fa-file-lines text-xs text-text-muted" />
                    }
                    title={name}
                  />
                )
              }}
            />
          </div>
        ))
      )}
    </div>
  )

  const detail = selectedFile ? (
    <div className="h-full overflow-y-auto p-4">
      <div className="flex items-center gap-2 mb-3">
        <Badge variant="outline">{selectedFile}</Badge>
      </div>
      <div className="text-sm leading-relaxed markdown-body">
        <MarkdownRenderer content={content} />
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
      sidebar={sidebar}
      detail={detail}
      mobileLabels={["Files", "Content"]}
      mobileTab={selectedFile !== null ? 1 : 0}
      onMobileTabChange={(tab) => {
        if (tab === 0) setSelectedFile(null)
      }}
    />
  )
}
