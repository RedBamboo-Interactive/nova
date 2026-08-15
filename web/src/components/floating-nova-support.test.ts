import assert from "node:assert/strict"
import test from "node:test"
import {
  getFloatingNovaSupport,
  isMobileClient,
  type FloatingNovaNavigator,
  type FloatingNovaWindow,
} from "./floating-nova-support.ts"

function desktopWindow(withPictureInPicture = true): FloatingNovaWindow {
  const candidate: { top?: unknown; documentPictureInPicture?: FloatingNovaWindow["documentPictureInPicture"] } = {}
  candidate.top = candidate
  if (withPictureInPicture) {
    candidate.documentPictureInPicture = {
      window: null,
      requestWindow: async () => { throw new Error("not used by support detection") },
    }
  }
  return candidate as FloatingNovaWindow
}

const desktopNavigator: FloatingNovaNavigator = { userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }

test("mobile detection recognizes client hints and mobile user agents", () => {
  assert.equal(isMobileClient(desktopNavigator), false)
  assert.equal(isMobileClient({ userAgent: desktopNavigator.userAgent, userAgentData: { mobile: true } }), true)
  assert.equal(isMobileClient({ userAgent: "Mozilla/5.0 (Linux; Android 16) Mobile" }), true)
})

test("Float Nova is absent outside a browser and on mobile clients", () => {
  assert.equal(getFloatingNovaSupport(undefined, undefined).reason, "browser_unavailable")
  assert.equal(getFloatingNovaSupport(desktopWindow(), { userAgent: desktopNavigator.userAgent, userAgentData: { mobile: true } }).reason, "mobile_not_supported")
  assert.equal(getFloatingNovaSupport(desktopWindow(), { userAgent: "Mozilla/5.0 (iPhone) Mobile" }).reason, "mobile_not_supported")
})

test("Float Nova requires top-level Document Picture-in-Picture support", () => {
  const framed: FloatingNovaWindow = { top: {}, documentPictureInPicture: desktopWindow().documentPictureInPicture }
  assert.equal(getFloatingNovaSupport(framed, desktopNavigator).reason, "top_level_window_required")
  assert.equal(getFloatingNovaSupport(desktopWindow(false), desktopNavigator).reason, "document_picture_in_picture_unavailable")
  assert.deepEqual(getFloatingNovaSupport(desktopWindow(), desktopNavigator), { supported: true })
})
