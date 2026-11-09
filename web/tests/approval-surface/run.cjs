const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path')
const { pathToFileURL } = require('node:url')
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))

// What a person on a phone (390 px) is offered when a Nova session parks on
// them. 2026-11-09: a Claude Bash approval showed up as a "Question" popup
// ("The assistant is asking a question...", a free-text box, "Send answer"),
// with no tool, no command and no Allow/Deny. An approval must reach the
// approval card on its tool call, and the question popup stays AskUserQuestion's.
const command = 'tail -25 "S:/Nova/03f50fd5/perm/deploy.log"; curl -s http://127.0.0.1:18804/health'
const permission = {
  toolName: 'Bash', toolUseId: 'toolu_bash', summary: command, input: { command },
  cwd: 'L:/Workspaces/Nova', options: ['allow_once', 'allow_session', 'deny'],
}
const askInput = { questions: [{ question: 'Which database should I use?', header: 'Database', multiSelect: false, options: [{ label: 'Postgres' }, { label: 'SQLite' }] }] }
// Enough text before the tool call that the conversation is taller than the phone.
const prose = Array.from({ length: 14 }, (_, i) => `Paragraph ${i + 1}: checking the deploy log and the live versions before anything else, so the next step is grounded.`).join('\n\n')

async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error('REDLEAF_SCRATCH_DIR required')
  const shots = path.join(scratch, 'approval-surface')
  fs.mkdirSync(shots, { recursive: true })
  const nova = path.resolve(__dirname, '../..')
  const { createServer } = await import(pathToFileURL(require.resolve('vite', { paths: [nova] })))
  const tailwind = (await import(pathToFileURL(require.resolve('@tailwindcss/vite', { paths: [path.join(shared, 'packages/ui')] })))).default
  const server = await createServer({ configFile: false, root: __dirname, plugins: [tailwind()], logLevel: 'error',
    cacheDir: path.join(scratch, 'approval-surface-vite-cache'),
    resolve: { alias: { react: path.join(nova, 'node_modules/react'), 'react-dom': path.join(nova, 'node_modules/react-dom') } },
    server: { host: '127.0.0.1', port: 0, fs: { allow: [path.resolve(nova, '..'), shared, scratch] } } })
  let browser
  const checks = [], observations = {}
  try {
    await server.listen(); browser = await chromium.launch({ headless: true })
    const open = async () => {
      const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true })
      const page = await context.newPage()
      const errors = []
      page.on('pageerror', e => errors.push(e.message))
      await page.goto(server.resolvedUrls.local[0])
      await page.getByRole('button', { name: 'Open LIVE' }).click()
      await page.waitForFunction(() => approvalTest.runtime?.activeDiscussion && !approvalTest.runtime.isLoadingMessages)
      await page.evaluate(prose => {
        approvalTest.stream({ type: 'status', content: 'active' })
        approvalTest.stream({ type: 'text', content: prose, messageId: 'msg-1' })
      }, prose)
      return { page, errors, close: () => context.close() }
    }
    const frames = page => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))
    const shot = (page, name) => page.screenshot({ path: path.join(shots, `${name}.png`) })
    const questionCard = page => page.locator('[data-slot="question-card"]')
    const questionDialog = page => page.locator('[data-question-dialog]')
    const approvalCard = page => page.locator('[data-slot="permission-approval-card"]')
    const inViewport = locator => locator.evaluate(element => {
      const box = element.getBoundingClientRect()
      return box.top >= 0 && box.bottom <= window.innerHeight && box.left >= 0 && box.right <= window.innerWidth
        && document.documentElement.scrollWidth <= window.innerWidth
    })
    const streamBash = page => page.evaluate(command => approvalTest.stream({ type: 'tool_use', toolName: 'Bash', toolInput: { command }, messageId: 'toolu_bash' }), command)
    const assertApprovalOffered = async (page, requestId) => {
      const card = approvalCard(page)
      await card.waitFor()
      assert.equal(await card.count(), 1)
      assert.equal(await card.getAttribute('data-outcome'), 'pending')
      assert.equal(await card.getAttribute('data-tool-use-id'), 'toolu_bash')
      const text = await card.innerText()
      assert(text.includes('Bash'), 'the tool is named')
      assert.equal(await card.locator('[data-slot="permission-approval-summary"]').innerText(), command, 'the command is shown')
      assert(text.includes('in L:/Workspaces/Nova'), 'the working directory is shown')
      const labels = (await card.locator('button').allInnerTexts()).map(label => label.trim())
      assert.deepEqual(labels, ['Allow once', 'Allow for this session', 'Deny'])
      assert.equal(await questionCard(page).count(), 0, 'no question card for an approval')
      assert.equal(await questionDialog(page).count(), 0, 'no question popup for an approval')
      assert(!(await page.locator('body').innerText()).includes('The assistant is asking a question'))
      assert.equal(await page.evaluate(() => approvalTest.runtime.pendingQuestion?.kind), 'permission')
      assert.equal(await page.evaluate(() => approvalTest.runtime.pendingQuestion?.requestId), requestId)
      await frames(page)
      assert(await inViewport(card), 'the card is on screen at 390 px, below a conversation taller than the phone')
      return card
    }

    // ── A live approval: the card on its tool call, decided through Nova's runtime ──
    {
      const { page, errors, close } = await open()
      await streamBash(page)
      await page.evaluate(permission => approvalTest.stream({
        type: 'question', kind: 'permission', requestId: 'req-perm', toolName: 'Bash',
        toolInput: permission.input, messageId: 'toolu_bash', permission,
      }), permission)
      const card = await assertApprovalOffered(page, 'req-perm')
      assert.equal(await page.locator('textarea').getAttribute('placeholder') !== 'Type your answer here...', true, 'typing is a message, not an answer')
      // With Nova's own status line nothing says "Waiting for your approval" below the
      // conversation: recorded, not asserted (the shared ChatPanel only draws its pending
      // line when the host has no renderStatusLine).
      observations.pendingLineWithNovaStatusLine = await page.locator('[data-slot="pending-question-line"]').count()
      await shot(page, '01-phone-live-approval')
      await card.getByRole('button', { name: 'Allow once' }).click()
      await page.waitForFunction(() => document.querySelector('[data-slot="permission-approval-card"]')?.getAttribute('data-outcome') === 'allow_once')
      assert.deepEqual(await page.evaluate(() => approvalTest.posts.filter(p => p.path.endsWith('/question'))),
        [{ path: '/ai-session/sessions/session-live/question', body: { requestId: 'req-perm', decision: 'allow_once' } }])
      assert.equal(await questionDialog(page).count(), 0)
      await shot(page, '02-phone-live-approval-allowed')
      assert.deepEqual(errors, [])
      await close()
      checks.push('live approval: card on the Bash call with tool, command, cwd and the three decisions; no question card or popup; Allow once posts {requestId, decision}')
    }

    // ── An approval whose live event lost kind: the next session read puts it right ──
    {
      const { page, errors, close } = await open()
      await streamBash(page)
      // The incident's shape: a `question` for the Bash call, without kind or permission.
      await page.evaluate(permission => approvalTest.stream({
        type: 'question', requestId: 'req-lost', toolName: 'Bash', toolInput: permission.input, messageId: 'toolu_bash',
      }), permission)
      await questionCard(page).first().waitFor()
      await questionCard(page).first().click()
      await questionDialog(page).waitFor()
      assert((await questionDialog(page).innerText()).includes('The assistant is asking a question...'), 'the fixture reproduces the incident')
      await shot(page, '03-phone-kind-lost-question-popup')
      await page.keyboard.press('Escape')
      await questionDialog(page).waitFor({ state: 'detached' })
      // A reload of the open discussion (no socket drop) reads the session: req-lost is an approval.
      await page.evaluate(permission => {
        approvalTest.snapshot = [{ requestId: 'req-lost', kind: 'permission', permission, toolName: 'Bash', toolUseId: 'toolu_bash', questions: [], input: permission.input, askedAt: '2026-11-09T13:57:31Z', expiresAt: '2026-11-09T14:27:31Z' }]
        approvalTest.runtime.reloadActiveMessages(true)
      }, permission)
      await page.waitForFunction(() => !approvalTest.runtime.isLoadingMessages && approvalTest.runtime.pendingQuestion?.kind === 'permission')
      await assertApprovalOffered(page, 'req-lost')
      await shot(page, '04-phone-kind-lost-healed')
      await approvalCard(page).getByRole('button', { name: 'Deny' }).click()
      await page.waitForFunction(() => document.querySelector('[data-slot="permission-approval-card"]')?.getAttribute('data-outcome') === 'deny')
      assert.deepEqual(await page.evaluate(() => approvalTest.posts.filter(p => p.path.endsWith('/question')).map(p => p.body)),
        [{ requestId: 'req-lost', decision: 'deny' }], 'no free-text answer was ever posted for the approval')
      assert.deepEqual(errors, [])
      await close()
      checks.push('kind lost on the live event: the session read turns the "Question" popup into the approval card for the same requestId')
    }

    // ── A reconnect (socket drop) while parked brings the card back ──
    {
      const { page, errors, close } = await open()
      await streamBash(page)
      await page.evaluate(permission => {
        approvalTest.stream({ type: 'question', kind: 'permission', requestId: 'req-perm', toolName: 'Bash', toolInput: permission.input, messageId: 'toolu_bash', permission })
        approvalTest.snapshot = [{ requestId: 'req-perm', kind: 'permission', permission, toolName: 'Bash', toolUseId: 'toolu_bash', questions: [], input: permission.input }]
        approvalTest.runtime.handleUpstreamDisconnect(); approvalTest.runtime.handleUpstreamReconnect()
      }, permission)
      await page.waitForFunction(() => !approvalTest.runtime.isLoadingMessages && approvalTest.runtime.pendingQuestion?.kind === 'permission')
      await assertApprovalOffered(page, 'req-perm')
      assert.deepEqual(errors, [])
      await close()
      checks.push('app switch (disconnect + reconnect) while parked: the approval card returns, never the question popup')
    }

    // ── AskUserQuestion is unchanged: its card and its popup ──
    {
      const { page, errors, close } = await open()
      await page.evaluate(askInput => {
        approvalTest.stream({ type: 'tool_use', toolName: 'AskUserQuestion', toolInput: askInput, messageId: 'toolu_ask' })
        approvalTest.stream({ type: 'question', requestId: 'req-ask', toolName: 'AskUserQuestion', toolInput: askInput, messageId: 'toolu_ask' })
      }, askInput)
      const card = questionCard(page).last()
      await card.waitFor()
      assert.equal(await card.getAttribute('data-status'), 'pending')
      assert.equal(await approvalCard(page).count(), 0, 'no approval card for a question')
      assert.equal(await page.evaluate(() => approvalTest.runtime.pendingQuestion?.kind ?? null), null)
      await card.click()
      const dialog = questionDialog(page)
      await dialog.waitFor()
      const text = await dialog.innerText()
      assert(text.includes('Question') && text.includes('Which database should I use?'))
      await dialog.getByText('Postgres', { exact: true }).click()
      await shot(page, '05-phone-ask-question-popup')
      await dialog.getByRole('button', { name: 'Send answer' }).click()
      await dialog.waitFor({ state: 'detached' })
      assert.deepEqual(await page.evaluate(() => approvalTest.posts.filter(p => p.path.endsWith('/question'))),
        [{ path: '/ai-session/sessions/session-live/question', body: { requestId: 'req-ask', answers: ['Postgres'] } }])
      assert.deepEqual(errors, [])
      await close()
      checks.push('AskUserQuestion: its card opens the "Question" popup and Send answer posts {requestId, answers}; no approval card')
    }
  } finally {
    await browser?.close(); await server.close()
    fs.writeFileSync(path.join(scratch, 'approval-surface-results.json'), JSON.stringify({ checks, observations }, null, 2))
  }
  for (const check of checks) console.log(`ok - ${check}`)
  console.log(`observations: ${JSON.stringify(observations)}`)
  console.log(`${checks.length} approval surface cases passed`)
}
main().catch(e => { console.error(e); process.exitCode = 1 })
