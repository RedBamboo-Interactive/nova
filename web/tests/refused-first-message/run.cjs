const assert = require('node:assert/strict')
const path = require('node:path')

// Live acceptance for 1df298f5: the first message of a new discussion that the server refuses
// (RedCompute draining for an update) stays visible as failed with Retry, survives a reload without
// being re-sent, and Retry stores and runs it under the same key. A second new discussion proves an
// ordinary first message is unchanged. Both discussions are created through the real shell route and
// archived at the end. The refusal is the server's exact 503 answer, replayed by intercepting only
// this discussion's /message call; everything else is the loaded product. The signed token stays in
// this process's loopback API calls: the browser signs in as the person, as it always does.
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))

const base = process.env.REDLEAF_BASE_URL || 'http://127.0.0.1:18804'
const token = process.env.REDLEAF_EXECUTION_TOKEN
if (!token) throw new Error('REDLEAF_EXECUTION_TOKEN is required for the loopback API calls')
const shots = process.env.NOVA_E2E_SHOTS
const REFUSAL = 'Not sent: an update is waiting to restart Leaf. Your draft is kept on this device. Retry after the update finishes.'
const PROMPT = 'Live delivery check, please ignore. Reply with the single word ok. Use no tools.'

async function api(method, route, body) {
  const response = await fetch(base + route, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  if (!response.ok) throw new Error(`${method} ${route}: ${response.status} ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

// The discussion read, not the session history: after the browser's send the session belongs to the
// person, and the agent token rightly cannot read it.
const history = id => api('GET', `/api/apps/nova/discussions/${id}`)
const userMessages = page => page.messages.filter(m => m.role === 'user')

// Stored: the server holds the person's message. Runs: the reply renders for the person, who
// owns the session transcript, and the discussion settles idle.
async function waitReplied(page, id) {
  await page.locator('[data-chat-assistant-text]').first().waitFor({ timeout: 180_000 })
  const deadline = Date.now() + 120_000
  for (;;) {
    const read = await history(id)
    if (userMessages(read).length >= 1 && read.discussion.status === 'idle') return read
    if (Date.now() > deadline) throw new Error(`discussion ${id} did not store and settle its first message`)
    await new Promise(resolve => setTimeout(resolve, 2000))
  }
}

async function newDiscussion(page) {
  await page.goto(`${base}/apps/nova/chat/new`, { waitUntil: 'domcontentloaded' })
  await page.waitForURL(url => /\/apps\/nova\/chat\/[0-9a-f]{8}$/.test(url.pathname), { timeout: 60_000 })
  const id = new URL(page.url()).pathname.split('/').pop()
  await page.locator('textarea').first().waitFor()
  return id
}

;(async () => {
  const browser = await chromium.launch({ headless: true })
  const created = []
  const errors = []
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
    const page = await context.newPage()
    page.on('pageerror', error => errors.push(`page: ${error.message}`))

    // 1. A refused first message.
    const refusedId = await newDiscussion(page)
    created.push(refusedId)
    const route = `**/api/apps/nova/discussions/${refusedId}/message`
    const posts = []
    page.on('request', request => {
      if (request.method() === 'POST' && request.url().endsWith(`/discussions/${refusedId}/message`))
        posts.push(request.headers()['x-idempotency-key'] ?? null)
    })
    await page.route(route, request => request.fulfill({
      status: 503, contentType: 'application/json',
      body: JSON.stringify({ error: 'maintenance_draining', message: REFUSAL }),
    }))
    await page.locator('textarea').first().fill(PROMPT)
    await page.locator('textarea').first().press('Enter')
    const ghost = page.locator('[data-slot="queued-message"][data-queue-state="failed"]')
    await ghost.waitFor({ timeout: 30_000 })
    await ghost.getByText(PROMPT).waitFor()
    assert.equal(posts.length, 1)
    const key = posts[0]
    assert.match(key, /^q-[0-9a-f]{32}$/)
    assert.equal(userMessages(await history(refusedId)).length, 0, 'nothing stored on the server')

    // 2. Reload: still shown as failed, and not re-sent.
    await page.reload({ waitUntil: 'domcontentloaded' })
    await ghost.waitFor({ timeout: 30_000 })
    await ghost.getByText(PROMPT).waitFor()
    await page.waitForLoadState('networkidle').catch(() => {})
    assert.equal(posts.length, 1, 'a refused message is never re-sent by a reload')
    assert(await ghost.getByRole('button', { name: 'Retry queued message', exact: true }).isEnabled())
    if (shots) await page.screenshot({ path: path.join(shots, 'refused-first-message-1280.png') })
    await page.setViewportSize({ width: 412, height: 900 })
    await ghost.waitFor()
    if (shots) await page.screenshot({ path: path.join(shots, 'refused-first-message-412.png') })
    await page.setViewportSize({ width: 1280, height: 900 })

    // 3. The update is over: Retry stores and runs it under the same key.
    await page.unroute(route)
    await ghost.getByRole('button', { name: 'Retry queued message', exact: true }).click()
    const refusedHistory = await waitReplied(page, refusedId)
    assert.equal(posts.length, 2)
    assert.equal(posts[1], key, 'retry keeps the idempotency key')
    const stored = userMessages(refusedHistory)
    assert.equal(stored.length, 1, 'stored exactly once')
    assert.equal(stored[0].messageUid, key.slice(2), 'the stored message keeps the client identity')
    await page.locator('[data-slot="queued-message"]').waitFor({ state: 'detached', timeout: 30_000 })
    assert.equal(await page.evaluate(id => localStorage.getItem(`rb-chat-queue:${id}`), refusedId), null)
    if (shots) await page.screenshot({ path: path.join(shots, 'refused-first-message-retried.png') })

    // 4. An ordinary first message is unchanged.
    const plainId = await newDiscussion(page)
    created.push(plainId)
    let sawFailed = false
    const watch = setInterval(() => {
      void page.locator('[data-slot="queued-message"][data-queue-state="failed"]').count()
        .then(count => { if (count) sawFailed = true }).catch(() => {})
    }, 200)
    await page.locator('textarea').first().fill(PROMPT)
    await page.locator('textarea').first().press('Enter')
    const plainHistory = await waitReplied(page, plainId)
    clearInterval(watch)
    assert.equal(sawFailed, false)
    assert.equal(userMessages(plainHistory).length, 1)
    await page.reload({ waitUntil: 'domcontentloaded' })
    await page.locator('[data-chat-user-bubble]').first().waitFor({ timeout: 30_000 })
    assert.equal(await page.locator('[data-slot="queued-message"]').count(), 0)

    assert.deepEqual(errors, [])
    console.log(JSON.stringify({ passed: true, refusedId, plainId, key, posts: posts.length }))
  } finally {
    await browser.close()
    for (const id of created)
      await api('DELETE', `/api/apps/nova/discussions/${id}`).catch(error => console.error(`archive ${id}: ${error.message}`))
  }
})().catch(error => { console.error(error); process.exitCode = 1 })
