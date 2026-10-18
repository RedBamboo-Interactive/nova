const { chromium } = require("playwright")

const baseUrl = process.env.REDLEAF_BASE_URL || "http://127.0.0.1:18804"
const knownTitle = process.env.NOVA_ARCHIVE_TITLE

if (!knownTitle) {
  throw new Error("NOVA_ARCHIVE_TITLE is required and must name a known archived chat")
}

;(async () => {
  const browser = await chromium.launch({ headless: true })
  const errors = []
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } })
    const page = await context.newPage()
    page.on("pageerror", error => errors.push(`page: ${error.message}`))
    page.on("console", message => {
      if (message.type() === "error") errors.push(`console: ${message.text()}`)
    })

    await page.goto(`${baseUrl}/apps/nova/chat`, { waitUntil: "domcontentloaded" })
    await page.getByRole("main").waitFor()

    const archiveToggle = page.getByRole("button", { name: "Browse archived discussions" })
    await archiveToggle.click()
    await page.locator('[data-archive-mode="true"]').waitFor()

    const search = page.getByRole("searchbox", { name: "Search archived discussions" })
    await search.fill(knownTitle)
    const row = page.locator('[data-discussion-status="archived"]').filter({ hasText: knownTitle }).first()
    await row.waitFor()
    await row.click()

    await page.locator('[data-slot="archived-discussion-bar"]').waitFor()
    await page.getByText("This discussion is archived", { exact: true }).waitFor()
    await page.getByRole("button", { name: "Reopen", exact: true }).waitFor()

    // Deliberately do not click Reopen against a real archived discussion.
    if (errors.length) throw new Error(errors.join("\n"))
    console.log(`Archive acceptance passed for ${knownTitle}; Reopen was not invoked.`)
  } finally {
    await browser.close()
  }
})().catch(error => {
  console.error(error)
  process.exit(1)
})
