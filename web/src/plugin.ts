import type { LeafAppPlugin } from "@redbamboo/utility"
import { NovaApp } from "./App"

export const plugin: LeafAppPlugin = {
  id: "nova",
  Page: NovaApp,
  commands: [
    // Discussion commands (new/switch/archive/search) register from inside the
    // page while it is mounted — they need the live discussion state. These two
    // only navigate, so they work from anywhere in the shell.
    {
      id: "open-chat",
      label: "Open Nova Chat",
      description: "Jump to Nova's chat panel",
      group: "Nova",
      keywords: ["nova", "chat", "talk", "companion"],
      action: ({ navigate }) => navigate("/apps/nova/chat"),
    },
    {
      id: "open-pulse",
      label: "Open Nova Pulse",
      description: "Jump to Nova's automations panel",
      group: "Nova",
      keywords: ["nova", "pulse", "automations", "heartbeats"],
      action: ({ navigate }) => navigate("/apps/nova/pulse"),
    },
  ],
}
