const assert = require("node:assert/strict")
const fs = require("node:fs")
const path = require("node:path")
const { pathToFileURL } = require("node:url")

const shared = path.resolve(__dirname, "../../../../redbamboo-packages")
const nova = path.resolve(__dirname, "../..")
const { chromium } = require(require.resolve("playwright", { paths: [path.join(shared, "packages/testing")] }))

async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error("REDLEAF_SCRATCH_DIR required")
  const { createServer } = await import(pathToFileURL(require.resolve("vite", { paths: [nova] })))
  const { default: react } = await import(pathToFileURL(require.resolve("@vitejs/plugin-react", { paths: [nova] })))
  const { default: tailwindcss } = await import(pathToFileURL(require.resolve("@tailwindcss/vite", { paths: [nova] })))
  const output = path.join(scratch, "voice-session-runtime")
  fs.mkdirSync(output, { recursive: true })
  const server = await createServer({
    configFile: false,
    root: __dirname,
    cacheDir: path.join(output, "vite-cache"),
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: {
        react: path.join(nova, "node_modules/react"),
        "react-dom": path.join(nova, "node_modules/react-dom"),
      },
    },
    server: {
      host: "127.0.0.1",
      port: 0,
      fs: { allow: [path.resolve(nova, ".."), shared, output] },
    },
  })
  let browser
  const results = []
  try {
    await server.listen()
    browser = await chromium.launch({ headless: true })
    for (const fixture of [
      { label: "desktop", viewport: { width: 1440, height: 1000 } },
      { label: "mobile", viewport: { width: 390, height: 844 } },
    ]) {
      const page = await browser.newPage({ viewport: fixture.viewport })
      const pageErrors = []
      const consoleErrors = []
      const paidRequests = []
      page.on("pageerror", error => pageErrors.push(error.message))
      page.on("console", message => {
        if (message.type() === "error") consoleErrors.push(message.text())
      })
      page.on("request", request => {
        const url = request.url()
        if (url.includes("/voice-session/sessions") || url.includes("api.openai.com")) paidRequests.push(url)
      })
      await page.addInitScript(() => {
        globalThis.__voiceWakeLock = { requests: 0, releases: 0 }
        Object.defineProperty(navigator, "wakeLock", {
          configurable: true,
          value: {
            request: async type => {
              if (type !== "screen") throw new Error(`unexpected wake lock ${type}`)
              globalThis.__voiceWakeLock.requests++
              let released = false
              let onRelease
              return {
                get released() { return released },
                async release() {
                  if (released) return
                  released = true
                  globalThis.__voiceWakeLock.releases++
                  onRelease?.()
                },
                addEventListener(_type, listener) { onRelease = listener },
              }
            },
          },
        })
      })
      await page.goto(server.resolvedUrls.local[0], { waitUntil: "networkidle" })
      const panel = page.getByRole("dialog", { name: "Voice conversation with Nova" })
      const discussion = page.getByRole("region", { name: "Live discussion" })
      await panel.waitFor()
      await discussion.waitFor()
      assert.deepEqual(await page.evaluate(() => globalThis.__voiceWakeLock), { requests: 1, releases: 0 })
      const discussionBody = discussion.locator(".nova-voice-mode__discussion-body")
      const discussionBodyStyle = await discussionBody.evaluate(element => {
        const style = getComputedStyle(element)
        return { maskImage: style.maskImage || style.webkitMaskImage }
      })
      assert.notEqual(discussionBodyStyle.maskImage, "none")
      assert.match(discussionBodyStyle.maskImage, /56px/)
      assert.equal(await discussion.getByText("Can you check what the real voice session is doing with my memory?").count(), 1)
      assert.equal(await discussion.getByText("I’m checking the canonical Nova session now.", { exact: false }).count(), 1)
      assert.equal(await discussion.getByText("Yes. This live voice is attached", { exact: false }).count(), 1)
      assert.equal(await discussion.getByText("voice-causal-context", { exact: false }).count(), 0,
        `${fixture.label}: private voice context must never render`)
      const acceptedUser = discussion.locator("[data-chat-user-bubble]")
        .filter({ hasText: "Can you check what the real voice session" })
      const assistantReply = discussion.locator(".markdown-body")
        .filter({ hasText: "I’m checking the canonical Nova session" })
      const assistantReplyHandle = await assistantReply.elementHandle()
      assert.ok(assistantReplyHandle)
      const userPrecedesReply = await acceptedUser.evaluate((user, reply) =>
        Boolean(user.compareDocumentPosition(reply) & Node.DOCUMENT_POSITION_FOLLOWING), assistantReplyHandle)
      assert.ok(
        userPrecedesReply,
        `${fixture.label}: accepted voice user turn must precede its assistant response`,
      )
      const toolSquare = discussion.getByRole("button", { name: "Read" })
      assert.equal(await toolSquare.count(), 1)
      const toolSquareStyle = await toolSquare.evaluate(element => {
        const style = getComputedStyle(element)
        return { width: style.width, height: style.height, borderWidth: style.borderWidth, padding: style.padding }
      })
      assert.deepEqual(toolSquareStyle, { width: "10px", height: "10px", borderWidth: "0px", padding: "0px" })
      await toolSquare.click()
      const toolDialog = page.getByRole("dialog", { name: "Read", exact: true })
      await toolDialog.waitFor()
      assert.equal(await toolDialog.getByText("Loaded the relevant memory index and voice-session decisions.", { exact: false }).count(), 1)
      await page.keyboard.press("Escape")
      await toolDialog.waitFor({ state: "hidden" })
      assert.equal(await page.getByTestId("end-count").textContent(), "0", "closing tool detail must not end voice mode")
      await toolSquare.evaluate(element => element.blur())
      const voiceSquares = discussion.getByRole("button", { name: "voice-session" })
      assert.equal(await voiceSquares.count(), 2, `${fixture.label}: start and end markers must both render`)
      for (let index = 0; index < 2; index++) {
        const markerStyle = await voiceSquares.nth(index).evaluate(element => {
          const style = getComputedStyle(element)
          return {
            width: style.width,
            height: style.height,
            borderRadius: style.borderRadius,
            borderWidth: style.borderWidth,
            backgroundColor: style.backgroundColor,
          }
        })
        assert.equal(markerStyle.width, "10px")
        assert.equal(markerStyle.height, "10px")
        assert.equal(markerStyle.borderRadius, "2px")
        assert.equal(markerStyle.borderWidth, "0px")
        assert.notEqual(markerStyle.backgroundColor, "rgba(0, 0, 0, 0)")
      }
      await voiceSquares.first().click()
      const startDialog = page.getByRole("dialog", { name: "voice-session", exact: true })
      await startDialog.waitFor()
      assert.equal(await startDialog.getByText("Natural Voice started", { exact: true }).count(), 1)
      assert.equal(await startDialog.getByText('\"state\": \"started\"', { exact: false }).count(), 1)
      await startDialog.focus()
      await page.keyboard.press("Escape")
      await startDialog.waitFor({ state: "hidden" })
      await voiceSquares.last().click()
      const endDialog = page.getByRole("dialog", { name: "voice-session", exact: true })
      await endDialog.waitFor()
      assert.equal(await endDialog.getByText("Natural Voice ended", { exact: true }).count(), 1)
      assert.equal(await endDialog.getByText('\"reason\": \"ended_by_user\"', { exact: false }).count(), 1)
      await endDialog.focus()
      await page.keyboard.press("Escape")
      await endDialog.waitFor({ state: "hidden" })
      assert.equal(await page.getByTestId("end-count").textContent(), "0", "event detail must not end voice mode")
      await voiceSquares.last().evaluate(element => element.blur())
      assert.equal(paidRequests.length, 0)
      assert.deepEqual(pageErrors, [])
      assert.deepEqual(consoleErrors, [])
      const bounds = await discussion.boundingBox()
      assert.ok(bounds && bounds.height >= 128, `${fixture.label}: discussion must remain usefully tall`)
      assert.ok(bounds && bounds.y + bounds.height <= fixture.viewport.height, `${fixture.label}: discussion must remain inside the viewport`)
      const layout = await page.evaluate(() => {
        const rect = selector => {
          const element = document.querySelector(selector)
          if (!element) return null
          const bounds = element.getBoundingClientRect()
          return { top: bounds.top, bottom: bounds.bottom, height: bounds.height }
        }
        const scroller = Array.from(document.querySelectorAll("[data-slot='chat-panel'] *"))
          .find(element => getComputedStyle(element).overflowY === "auto")
        return {
          main: rect(".nova-voice-mode main"),
          identity: rect(".nova-voice-mode__identity"),
          discussion: rect(".nova-voice-mode__discussion"),
          footer: rect(".nova-voice-mode footer"),
          scroller: scroller ? {
            clientHeight: scroller.clientHeight,
            scrollHeight: scroller.scrollHeight,
            scrollTop: scroller.scrollTop,
          } : null,
        }
      })
      assert.ok(layout.identity && layout.identity.top >= 0, `${fixture.label}: portrait must remain inside the viewport`)
      assert.ok(layout.scroller, `${fixture.label}: discussion must expose its own scroller`)
      if (fixture.label === "mobile") {
        assert.ok(layout.scroller.scrollHeight > layout.scroller.clientHeight, "mobile: long discussion must scroll in place")
      }
      await page.screenshot({ path: path.join(output, `${fixture.label}.png`), fullPage: true })
      await panel.getByRole("button", { name: "End voice session" }).focus()
      await page.keyboard.press("Escape")
      assert.equal(await page.getByTestId("end-count").textContent(), "1", "voice mode itself must retain Escape-to-end")
      await page.waitForFunction(() => globalThis.__voiceWakeLock.releases === 1)
      const wakeLock = await page.evaluate(() => globalThis.__voiceWakeLock)
      results.push({ label: fixture.label, bounds, layout, discussionBodyStyle, toolSquareStyle, wakeLock, paidRequests, pageErrors, consoleErrors })
      await page.close()
    }
  } finally {
    await browser?.close()
    await server.close()
  }
  fs.writeFileSync(path.join(output, "results.json"), JSON.stringify(results, null, 2))
  console.log(JSON.stringify(results, null, 2))
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
