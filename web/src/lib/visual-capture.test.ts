import test from "node:test"
import assert from "node:assert/strict"
import { monitorCaptureToContext } from "./visual-capture-core.ts"

test("monitor capture context describes the selected monitor and capture-time windows", () => {
  const context = monitorCaptureToContext({
    id: "monitor-a1",
    name: "Monitor 2",
    deviceName: "DISPLAY2",
    primary: false,
    bounds: { left: 1920, top: 0, width: 2560, height: 1440 },
    capturedAt: "2026-08-15T14:00:00Z",
    mediaType: "image/png",
    base64: "pixels",
    applications: ["Code", "firefox"],
    windows: [{
      application: "Code",
      title: "Visual Capture",
      processId: 42,
      bounds: { left: 2000, top: 20, width: 1200, height: 900 },
    }],
  })

  assert.equal(context.app, "Monitor 2")
  assert.equal(context.url, "screen://local/monitor-a1")
  assert.equal(context.description, "2560×1440 · Code, firefox")
  assert.equal(context.screenshot?.base64, "pixels")
  assert.deepEqual(context.extra?.applications, ["Code", "firefox"])
  assert.equal((context.extra?.visibleWindows as unknown[]).length, 1)
})
