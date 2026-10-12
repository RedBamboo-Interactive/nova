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
      await page.goto(server.resolvedUrls.local[0], { waitUntil: "networkidle" })
      const panel = page.getByRole("dialog", { name: "Voice conversation with Nova" })
      const discussion = page.getByRole("region", { name: "Live discussion" })
      await panel.waitFor()
      await discussion.waitFor()
      assert.equal(await discussion.getByText("Can you check what the real voice session is doing with my memory?").count(), 1)
      assert.equal(await discussion.getByText("I’m checking the canonical Nova session now.", { exact: false }).count(), 1)
      assert.equal(await discussion.getByText("Yes. This live voice is attached", { exact: false }).count(), 1)
      const toolSquare = discussion.getByRole("button", { name: "Read" })
      assert.equal(await toolSquare.count(), 1)
      await toolSquare.click()
      const toolDialog = page.getByRole("dialog", { name: "Read", exact: true })
      await toolDialog.waitFor()
      assert.equal(await toolDialog.getByText("Loaded the relevant memory index and voice-session decisions.", { exact: false }).count(), 1)
      await page.keyboard.press("Escape")
      await toolDialog.waitFor({ state: "hidden" })
      assert.equal(await page.getByTestId("end-count").textContent(), "0", "closing tool detail must not end voice mode")
      assert.equal(paidRequests.length, 0)
      assert.deepEqual(pageErrors, [])
      assert.deepEqual(consoleErrors, [])
      const bounds = await discussion.boundingBox()
      assert.ok(bounds && bounds.height >= 128, `${fixture.label}: discussion must remain usefully tall`)
      assert.ok(bounds && bounds.y + bounds.height <= fixture.viewport.height, `${fixture.label}: discussion must remain inside the viewport`)
      await page.screenshot({ path: path.join(output, `${fixture.label}.png`), fullPage: true })
      await page.keyboard.press("Escape")
      assert.equal(await page.getByTestId("end-count").textContent(), "1", "voice mode itself must retain Escape-to-end")
      results.push({ label: fixture.label, bounds, paidRequests, pageErrors, consoleErrors })
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
