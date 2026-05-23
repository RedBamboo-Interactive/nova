import { useState, useCallback, useEffect, useRef } from "react"
import { api } from "@/lib/api"
import type { DiscussionInfo, DiscussionMessage, DiscussionSendResponse } from "@/lib/types"
import type { MessageBlock, MessagePart } from "@redbamboo/chat"

function toChatMessages(messages: DiscussionMessage[]): MessageBlock[] {
  return messages.map((m) => ({
    id: m.id,
    role: m.role,
    parts: m.parts.map((p): MessagePart => ({
      type: p.type === "tool_use" || p.type === "tool_result" ? p.type : "text",
      content: p.content,
      toolName: p.toolName,
      toolInput: p.toolInput,
    })),
    timestamp: m.timestamp,
  }))
}

export function useDiscussions() {
  const [discussions, setDiscussions] = useState<DiscussionInfo[]>([])
  const [activeDiscussionId, setActiveDiscussionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, MessageBlock[]>>({})
  const [streaming, setStreaming] = useState<Record<string, boolean>>({})
  const [dismissedIds, setDismissedIds] = useState<Set<string>>(new Set())
  const loadedRef = useRef<Set<string>>(new Set())

  const activeDiscussion = discussions.find((d) => d.id === activeDiscussionId) ?? null
  const activeMessages = activeDiscussionId ? messages[activeDiscussionId] ?? [] : []
  const isStreaming = activeDiscussionId ? streaming[activeDiscussionId] ?? false : false

  const refreshDiscussions = useCallback(async () => {
    const list = await api.get<DiscussionInfo[]>("/api/discussions")
    setDiscussions(list.filter((d) => !dismissedIds.has(d.id)))
  }, [dismissedIds])

  useEffect(() => {
    refreshDiscussions()
  }, [refreshDiscussions])

  useEffect(() => {
    const onVisibility = () => {
      if (document.visibilityState === "visible") refreshDiscussions()
    }
    document.addEventListener("visibilitychange", onVisibility)
    return () => document.removeEventListener("visibilitychange", onVisibility)
  }, [refreshDiscussions])

  const loadMessages = useCallback(async (id: string) => {
    if (loadedRef.current.has(id)) return
    loadedRef.current.add(id)
    const data = await api.get<{ discussion: DiscussionInfo; messages: DiscussionMessage[] }>(`/api/discussions/${id}`)
    setMessages((prev) => ({ ...prev, [id]: toChatMessages(data.messages) }))
  }, [])

  const selectDiscussion = useCallback((id: string) => {
    setActiveDiscussionId(id)
    loadMessages(id)
  }, [loadMessages])

  const createDiscussion = useCallback(async () => {
    const d = await api.post<DiscussionInfo>("/api/discussions")
    setDiscussions((prev) => [d, ...prev])
    setActiveDiscussionId(d.id)
    setMessages((prev) => ({ ...prev, [d.id]: [] }))
    loadedRef.current.add(d.id)
    return d
  }, [])

  const sendMessage = useCallback(async (discussionId: string, content: string) => {
    const userMsg: MessageBlock = {
      id: crypto.randomUUID(),
      role: "user",
      parts: [{ type: "text", content }],
      timestamp: new Date().toISOString(),
    }
    setMessages((prev) => ({
      ...prev,
      [discussionId]: [...(prev[discussionId] ?? []), userMsg],
    }))

    setStreaming((prev) => ({ ...prev, [discussionId]: true }))
    setDiscussions((prev) =>
      prev.map((d) => d.id === discussionId ? { ...d, status: "thinking" as const } : d)
    )

    try {
      const response = await api.post<DiscussionSendResponse>(`/api/discussions/${discussionId}/send`, {
        message: content,
      })

      const parts: MessagePart[] = []
      if (response.toolCalls) {
        for (const tc of response.toolCalls) {
          parts.push({ type: "tool_use", content: tc.output ?? "", toolName: tc.name, toolInput: tc.input ?? "" })
          if (tc.output) {
            parts.push({ type: "tool_result", content: tc.output, toolName: tc.name })
          }
        }
      }
      parts.push({ type: "text", content: response.text })

      const assistantMsg: MessageBlock = {
        id: crypto.randomUUID(),
        role: "assistant",
        parts,
        timestamp: new Date().toISOString(),
      }
      setMessages((prev) => ({
        ...prev,
        [discussionId]: [...(prev[discussionId] ?? []), assistantMsg],
      }))

      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId
          ? { ...d, status: "idle" as const, title: response.title ?? d.title, messageCount: d.messageCount + 2 }
          : d
        )
      )
    } catch (err) {
      const errorMsg: MessageBlock = {
        id: crypto.randomUUID(),
        role: "assistant",
        parts: [{ type: "error", content: err instanceof Error ? err.message : "Unknown error" }],
        timestamp: new Date().toISOString(),
      }
      setMessages((prev) => ({
        ...prev,
        [discussionId]: [...(prev[discussionId] ?? []), errorMsg],
      }))
      setDiscussions((prev) =>
        prev.map((d) => d.id === discussionId ? { ...d, status: "idle" as const } : d)
      )
    } finally {
      setStreaming((prev) => ({ ...prev, [discussionId]: false }))
    }
  }, [])

  const archiveDiscussion = useCallback(async (id: string) => {
    await api.delete(`/api/discussions/${id}`)
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, status: "archived" as const } : d))
  }, [])

  const dismissDiscussion = useCallback((id: string) => {
    setDismissedIds((prev) => new Set(prev).add(id))
    setDiscussions((prev) => prev.filter((d) => d.id !== id))
    if (activeDiscussionId === id) setActiveDiscussionId(null)
  }, [activeDiscussionId])

  const renameDiscussion = useCallback(async (id: string, title: string) => {
    await api.put(`/api/discussions/${id}/title`, { title })
    setDiscussions((prev) => prev.map((d) => d.id === id ? { ...d, title } : d))
  }, [])

  const visibleDiscussions = discussions.filter((d) => d.status !== "archived")

  return {
    discussions: visibleDiscussions,
    activeDiscussion,
    activeDiscussionId,
    activeMessages,
    isStreaming,
    selectDiscussion,
    createDiscussion,
    sendMessage,
    archiveDiscussion,
    dismissDiscussion,
    renameDiscussion,
    refreshDiscussions,
  }
}
