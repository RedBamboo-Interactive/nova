const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')

const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', {
  paths: [path.join(shared, 'packages/testing')],
}))

const discussionId = '87d23385'
const discussion = {
  id: discussionId,
  entityId: 'history-load-fixture',
  title: 'Cinderwake Report Iteration Package Context',
  titleSource: 'manual',
  sessionId: null,
  status: 'idle',
  type: 'chat',
  createdAt: '2026-10-18T08:00:00.000Z',
  lastActivity: '2026-10-18T08:00:00.000Z',
  messageCount: 1,
  lastReadAt: null,
  conversationRevision: 1,
  readConversationRevision: 0,
  agentId: null,
}

const recoveredPage = {
  discussion,
  session: null,
  messages: [{
    id: 'history-recovered-record',
    role: 'assistant',
    eventType: 'text',
    content: 'History recovered after Retry.',
    messageUid: 'history-recovered-turn',
    phase: 'final_answer',
    timestamp: '2026-10-18T08:00:01.000Z',
    epoch: 'history-load-fixture-epoch',
    sequence: 1,
  }],
  overlays: [],
  page: {
    direction: 'newest',
    epoch: 'history-load-fixture-epoch',
    oldestCursor: 'history-load-fixture-oldest',
    newestCursor: 'history-load-fixture-newest',
    hasEarlier: false,
    hasLater: false,
    fromSequence: 1,
    throughSequence: 1,
    boundaryComplete: true,
  },
}

function contentType(file) {
  if (file.endsWith('.css')) return 'text/css'
  if (file.endsWith('.js')) return 'text/javascript'
  if (file.endsWith('.json')) return 'application/json'
  return 'application/octet-stream'
}

async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error('REDLEAF_SCRATCH_DIR required')
  const output = path.join(scratch, `nova-history-load-error-${Date.now()}`)
  fs.mkdirSync(output, { recursive: true })

  const dist = path.resolve(__dirname, '../../dist')
  const browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const page = await context.newPage()
  const pageErrors = []
  const consoleErrors = []
  const failedRequests = []
  let allowRecovery = false
  let historyRequests = 0

  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text())
  })
  page.on('requestfailed', request => {
    failedRequests.push(`${request.method()} ${request.url()}: ${request.failure()?.errorText ?? 'failed'}`)
  })

  await page.route('**/*', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const novaAssetPrefix = '/plugin-assets/nova/'
    if (url.pathname.startsWith(novaAssetPrefix)) {
      const relative = decodeURIComponent(url.pathname.slice(novaAssetPrefix.length))
      const file = path.resolve(dist, relative)
      const relativeToDist = path.relative(dist, file)
      if (!relativeToDist.startsWith('..') && !path.isAbsolute(relativeToDist) && fs.existsSync(file)) {
        await route.fulfill({ path: file, contentType: contentType(file) })
        return
      }
    }

    if (url.pathname === '/api/apps/nova/discussions' && request.method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([discussion]) })
      return
    }
    if (url.pathname === '/api/apps/nova/discussions/sync' && request.method() === 'POST') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' })
      return
    }
    if (url.pathname === `/api/apps/nova/discussions/${discussionId}/read`) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(discussion) })
      return
    }
    if (url.pathname === `/api/apps/nova/discussions/${discussionId}/input-queue`) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], queue: { depth: 0, state: 'empty' } }),
      })
      return
    }
    if (url.pathname === '/api/apps/nova/discussions/delegations') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' })
      return
    }
    if (url.pathname === `/api/apps/nova/discussions/${discussionId}/history-page`) {
      historyRequests++
      if (!allowRecovery) {
        await route.fulfill({
          status: 413,
          contentType: 'application/json',
          body: JSON.stringify({
            error: 'transcript_message_too_large',
            message: 'One message in this discussion is too large to load.',
          }),
        })
      } else {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(recoveredPage) })
      }
      return
    }
    await route.continue()
  })

  const results = []
  try {
    for (const viewport of [
      { name: 'desktop', width: 1440, height: 1000 },
      { name: 'phone', width: 390, height: 844 },
    ]) {
      allowRecovery = false
      await page.setViewportSize({ width: viewport.width, height: viewport.height })
      await page.goto(`http://127.0.0.1:18804/apps/nova/chat/${discussionId}`, { waitUntil: 'domcontentloaded' })

      const errorState = page.locator('[data-slot="history-load-error"]')
      await errorState.waitFor({ state: 'visible' })
      await assert.doesNotReject(() => errorState.getByText('One message in this discussion is too large to load.').waitFor())
      const errorScreenshot = path.join(output, `${viewport.name}-error.png`)
      await page.screenshot({ path: errorScreenshot })

      allowRecovery = true
      await errorState.getByRole('button', { name: 'Retry' }).click()
      await errorState.waitFor({ state: 'hidden' })
      await page.getByText('History recovered after Retry.').waitFor({ state: 'visible' })
      await page.waitForLoadState('networkidle')
      const recoveryScreenshot = path.join(output, `${viewport.name}-recovery.png`)
      await page.screenshot({ path: recoveryScreenshot })
      results.push({ ...viewport, errorScreenshot, recoveryScreenshot })
    }

    assert.ok(historyRequests >= 4, `expected an initial request and Retry at both viewports, got ${historyRequests}`)
    const expectedTransportDiagnostics = consoleErrors.filter(message =>
      message === 'Failed to load resource: the server responded with a status of 413 (Payload Too Large)')
    const unexpectedConsoleErrors = consoleErrors.filter(message => !expectedTransportDiagnostics.includes(message))
    const expectedQueueCancellations = failedRequests.filter(message =>
      message.includes(`/api/apps/nova/discussions/${discussionId}/input-queue?includeTerminal=true: net::ERR_ABORTED`))
    const unexpectedFailedRequests = failedRequests.filter(message => !expectedQueueCancellations.includes(message))
    assert.deepEqual(pageErrors, [])
    assert.deepEqual(unexpectedConsoleErrors, [])
    assert.deepEqual(unexpectedFailedRequests, [])
    const report = {
      historyRequests,
      pageErrors,
      unexpectedConsoleErrors,
      expectedTransportDiagnostics,
      unexpectedFailedRequests,
      expectedQueueCancellations,
      results,
    }
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(report, null, 2))
    console.log(JSON.stringify({ output, ...report }, null, 2))
  } finally {
    await context.close()
    await browser.close()
  }
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
