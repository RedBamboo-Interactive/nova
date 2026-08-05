import type { LeafAppPlugin } from "@redbamboo/utility"
import { NovaApp } from "./App"
import { NovaSettingsPanel } from "./components/NovaSettingsPanel"
import {
  FLOATING_NOVA_COMMAND_ID,
  FLOATING_NOVA_SHORTCUT,
  FLOATING_NOVA_SURFACE_ID,
  FloatingNovaService,
} from "./components/floating-nova-service"
import { getFloatingNovaSupport } from "./components/floating-nova-support"
import { runUiSurfaceAction } from "@redbamboo/utility"

export const plugin: LeafAppPlugin = {
  id: "nova",
  Page: NovaApp,
  shellServices: { "floating-chat-controller": FloatingNovaService },
  commands: getFloatingNovaSupport().supported ? [{
    id: FLOATING_NOVA_COMMAND_ID,
    label: "Float Nova",
    description: "Open or focus a compact Nova chat in a desktop always-on-top window.",
    group: "AI",
    shortcut: FLOATING_NOVA_SHORTCUT,
    keywords: ["nova", "floating", "overlay", "always on top", "picture in picture", "pip", "companion"],
    requiresUserActivation: true,
    targetSurfaceId: FLOATING_NOVA_SURFACE_ID,
    action: async () => { await runUiSurfaceAction(FLOATING_NOVA_SURFACE_ID, "open") },
  }] : [],
  settingsPanel: { Component: NovaSettingsPanel },
}
