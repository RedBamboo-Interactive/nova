import { Navigate, type RouteObject } from "react-router-dom"
import { ChatView } from "./panels/ChatView"
import { AutomationsPanel } from "./panels/HeartbeatsPanel"
import { MemoryPanel } from "./panels/MemoryPanel"

export const routes: RouteObject[] = [
  { index: true, element: <Navigate to="chat" replace /> },
  {
    path: "chat",
    handle: { crumb: "Chat", icon: "fa-solid fa-comment" },
    children: [
      { index: true, element: <ChatView /> },
      {
        path: ":discussionId",
        handle: { crumb: (p: Record<string, string>) => p.discussionId },
        element: <ChatView />,
      },
    ],
  },
  {
    path: "pulse",
    handle: { crumb: "Pulse", icon: "fa-solid fa-heart-pulse" },
    children: [
      { index: true, element: <AutomationsPanel /> },
      {
        path: ":automationName",
        handle: { crumb: (p: Record<string, string>) => p.automationName },
        element: <AutomationsPanel />,
      },
    ],
  },
  {
    path: "journal",
    handle: { crumb: "Journal", icon: "fa-solid fa-book" },
    children: [
      { index: true, element: <MemoryPanel /> },
      {
        path: "*",
        element: <MemoryPanel />,
      },
    ],
  },
]
