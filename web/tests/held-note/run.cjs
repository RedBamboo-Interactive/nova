const assert = require('node:assert/strict')
const path = require('node:path')

// Live acceptance for held notes (delivery with-next) on the real RedLeaf shell. It opens one
// disposable discussion and archives it at the end. The signed token stays in this process's
// loopback API calls: the browser signs in as the person, as it always does.
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))

const base = process.env.REDLEAF_BASE_URL || 'http://127.0.0.1:18804'
const token = process.env.REDLEAF_EXECUTION_TOKEN
if (!token) throw new Error('REDLEAF_EXECUTION_TOKEN is required for the loopback API calls')
const HELD = 'Held: it joins the next turn'

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

async function waitIdle(id, assistantTexts) {
  const deadline = Date.now() + 120_000
  for (;;) {
    const page = await api('GET', `/api/apps/nova/discussions/${id}/history-page?limit=100`)
    const replies = page.messages.filter(m => m.role === 'assistant' && m.eventType === 'text').length
    if (page.discussion.status === 'idle' && replies >= assistantTexts) return
    if (Date.now() > deadline) throw new Error(`discussion ${id} did not settle`)
    await new Promise(resolve => setTimeout(resolve, 2000))
  }
}

;(async () => {
  const created = await api('POST', '/api/apps/nova/discussions', {
    title: 'Held note e2e (test, archive after)',
    brief: 'Delivery test. For every note, reply with one line listing the code words of all notes so far. Use no tools. Now reply: ready.',
    idempotencyKey: `held-note-e2e-${Date.now()}`,
  })
  const id = created.id
  const browser = await chromium.launch({ headless: true })
  const errors = []
  try {
    await waitIdle(id, 1)
    const held = await api('POST', `/api/apps/nova/discussions/${id}/event`, { content: 'Held note. Code word: cobalt.', delivery: 'with-next' })
    assert.equal(held.delivery, 'held')

    const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
    page.on('pageerror', error => errors.push(`page: ${error.message}`))
    await page.goto(`${base}/apps/nova/chat/${id}`, { waitUntil: 'domcontentloaded' })
    const heldMarker = page.locator(`[data-slot="agent-note-marker"][data-input-message-uid="${held.messageUid}"]`)

    // Held: drawn hollow, and says so.
    await heldMarker.waitFor({ timeout: 30_000 })
    assert.equal(await heldMarker.getAttribute('data-pending'), 'true')
    assert.match(await heldMarker.getAttribute('aria-label'), new RegExp(HELD))

    // An ordinary note starts the turn; the held one joins it and stops looking held, live.
    const plain = await api('POST', `/api/apps/nova/discussions/${id}/event`, { content: 'Ordinary note. Code word: amber.', delivery: 'after-current' })
    await page.waitForFunction(uid => {
      const marker = document.querySelector(`[data-slot="agent-note-marker"][data-input-message-uid="${uid}"]`)
      return marker && !marker.hasAttribute('data-pending')
    }, held.messageUid, { timeout: 60_000 })
    const plainMarker = page.locator(`[data-slot="agent-note-marker"][data-input-message-uid="${plain.messageUid}"]`)
    await plainMarker.waitFor()
    assert.equal(await plainMarker.getAttribute('data-pending'), null)
    assert.doesNotMatch(await heldMarker.getAttribute('aria-label'), new RegExp(HELD))

    // The server agrees after a reload.
    await waitIdle(id, 2)
    await page.reload({ waitUntil: 'domcontentloaded' })
    await heldMarker.waitFor({ timeout: 30_000 })
    assert.equal(await heldMarker.getAttribute('data-pending'), null)

    if (errors.length) throw new Error(errors.join('\n'))
    console.log(`Held note acceptance passed in ${id}: held, then joined the next turn.`)
  } finally {
    await browser.close()
    await api('DELETE', `/api/apps/nova/discussions/${id}`).catch(error => console.error(`archive ${id}: ${error.message}`))
  }
})().catch(error => {
  console.error(error)
  process.exit(1)
})
