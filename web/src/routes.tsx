import { Navigate, type RouteObject } from "react-router-dom"
import { ChatView } from "./panels/ChatView"
import { MemoryPanel } from "./panels/MemoryPanel"
import { NewDiscussionRoute } from "./panels/NewDiscussionRoute"

export const routes: RouteObject[] = [
  { index: true, element: <Navigate to="chat" replace /> },
  {
    path: "chat",
    handle: { crumb: "Chat", icon: "ph-bold ph-chat-circle" },
    children: [
      { index: true, element: <ChatView /> },
      // Before :discussionId: "new" opens a fresh discussion with an optional unsent draft.
      {
        path: "new",
        handle: { crumb: "New discussion" },
        element: <NewDiscussionRoute />,
      },
      // Before :discussionId, for the same reason. It is the same view as a discussion, so going
      // between them keeps the chat's state, exactly as going between Live and Heartbeat does.
      {
        path: "journal/:date?",
        handle: { crumb: "Journal" },
        element: <ChatView />,
      },
      {
        path: ":discussionId",
        handle: { crumb: (p: Record<string, string>) => p.discussionId },
        element: <ChatView />,
      },
    ],
  },
  {
    path: "journal/*",
    handle: { crumb: "Memory", icon: "ph-bold ph-book-open-text" },
    element: <MemoryPanel />,
  },
  { path: "*", element: <Navigate to="/apps/nova/chat" replace /> },
]
