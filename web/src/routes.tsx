import { Navigate, type RouteObject } from "react-router-dom"
import { ChatView } from "./panels/ChatView"

export const routes: RouteObject[] = [
  { index: true, element: <Navigate to="chat" replace /> },
  {
    path: "chat",
    handle: { crumb: "Chat", icon: "ph-bold ph-chat-circle" },
    children: [
      { index: true, element: <ChatView /> },
      {
        path: ":discussionId",
        handle: { crumb: (p: Record<string, string>) => p.discussionId },
        element: <ChatView />,
      },
    ],
  },
  { path: "*", element: <Navigate to="/apps/nova/chat" replace /> },
]
