import test from "node:test"
import assert from "node:assert/strict"
import { shouldCloseVoiceWhenHidden } from "./voice-session-lifecycle.ts"

const desktop = { userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }

test("desktop visibility changes preserve an intentional voice call", () => {
  assert.equal(shouldCloseVoiceWhenHidden("hidden", desktop), false)
  assert.equal(shouldCloseVoiceWhenHidden("visible", desktop), false)
})

test("mobile backgrounding remains a voice billing boundary", () => {
  assert.equal(shouldCloseVoiceWhenHidden("hidden", {
    userAgent: "Mozilla/5.0 (Linux; Android 16) Mobile",
  }), true)
  assert.equal(shouldCloseVoiceWhenHidden("hidden", {
    userAgent: desktop.userAgent,
    userAgentData: { mobile: true },
  }), true)
  assert.equal(shouldCloseVoiceWhenHidden("visible", {
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) Mobile",
  }), false)
})
