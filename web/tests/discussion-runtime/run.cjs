const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path')
const { pathToFileURL } = require('node:url')
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))
async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error('REDLEAF_SCRATCH_DIR required')
  const nova = path.resolve(__dirname, '../..')
  const { createServer } = await import(pathToFileURL(require.resolve('vite', { paths: [nova] })))
  const server = await createServer({ configFile: false, root: __dirname, cacheDir: path.join(scratch, 'runtime-vite-cache'),
    resolve: { alias: { react: path.join(nova, 'node_modules/react'), 'react-dom': path.join(nova, 'node_modules/react-dom') } },
    server: { host: '127.0.0.1', port: 0, fs: { allow: [path.resolve(nova, '..'), shared, scratch] } } })
  let browser; const results = []
  try {
    await server.listen(); browser = await chromium.launch({ headless: true })
    for (const legacy of [false, true]) {
      const page = await browser.newPage(); const errors = [], consoleErrors = []
      page.on('pageerror', e => errors.push(e.message))
      page.on('console', e => { if (e.type() === 'error') consoleErrors.push(e.text()) })
      const observations = { scenario: 'console-observations', legacy, pageErrors: errors, consoleErrors, knownUpdaterWarning: [] }
      results.push(observations)
      await page.goto(server.resolvedUrls.local[0] + (legacy ? '?legacy' : ''))
      await page.getByRole('button', { name: 'Open LIVE' }).click()
      await page.waitForFunction(() => runtimeTest.runtime?.isStreaming === true, null, { timeout: 5000 })
      results.push({ scenario: 'fresh-active-mount', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.runtime.handleUpstreamDisconnect(); runtimeTest.runtime.handleUpstreamReconnect() })
      await page.waitForFunction(() => runtimeTest.runtime.isStreaming === true)
      results.push({ scenario: 'reconnect-already-active', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.hold = true; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => runtimeTest.pending.length > 0)
      await page.evaluate(() => { runtimeTest.event('Idle'); runtimeTest.pending.shift()(); runtimeTest.hold = false })
      await page.waitForFunction(() => runtimeTest.runtime.isStreaming === false)
      await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false, 'stale Active snapshot must not reverse Idle')
      results.push({ scenario: 'terminal-beats-stale-snapshot', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.event('Active'); runtimeTest.hold = true; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => runtimeTest.pending.length > 0)
      await page.evaluate(() => { runtimeTest.stream({ type: 'status', content: 'completed' }); runtimeTest.pending.shift()(); runtimeTest.hold = false })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages && !runtimeTest.runtime.isStreaming)
      results.push({ scenario: 'terminal-stream-beats-stale-snapshot', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.status = 'Idle'; runtimeTest.hold = true; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => runtimeTest.pending.length > 0)
      await page.evaluate(() => { runtimeTest.event('Active'); runtimeTest.pending.shift()(); runtimeTest.hold = false })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages)
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'new Active beats stale Idle')
      await page.evaluate(() => runtimeTest.runtime.refreshDiscussions())
      assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.status), 'thinking', 'stale other-client list cannot advertise idle')
      results.push({ scenario: 'new-start-and-list-races', legacy, passed: true })
      await page.evaluate(() => runtimeTest.runtime.renameDiscussion('live', 'metadata fixture'))
      assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.status), 'thinking', 'metadata ack cannot reset running lifecycle')
      results.push({ scenario: 'metadata-ack-preserves-runtime', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.event('Idle'); runtimeTest.status = 'Idle' })
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      await page.evaluate(() => {
        runtimeTest.admission = { accepted: true, disposition: 'queued', queue: { state: 'waiting_for_session', blockedReason: 'maintenance_drain' } }
        return runtimeTest.runtime.sendMessage('live', 'maintenance fixture', undefined, { idempotencyKey: 'maintenance', messageUid: 'maintenance-uid' })
      })
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming, null, { timeout: 5000 })
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false, 'authoritative waiting on Idle must settle only the fictitious optimistic turn')
      assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.status), 'idle')
      results.push({ scenario: 'maintenance-waiting-idle-admission', legacy, passed: true })
      await page.evaluate(() => {
        runtimeTest.admission = { accepted: true, disposition: 'queued', queue: { state: 'ready', blockedReason: null } }
        return runtimeTest.runtime.sendMessage('live', 'ready fixture', undefined, { idempotencyKey: 'ready', messageUid: 'ready-uid' })
      })
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'ready Idle admission retains optimistic turn without flicker')
      results.push({ scenario: 'ready-idle-admission-grace', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.event('Active'); runtimeTest.event('Idle') })
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      for (const reason of ['maintenance_drain', 'active_turn']) {
        await page.evaluate(reason => {
          runtimeTest.status = 'Active'
          runtimeTest.admission = { accepted: true, disposition: 'queued', queue: { state: 'waiting_for_session', blockedReason: reason } }
          return runtimeTest.runtime.sendMessage('live', 'working fixture', undefined, { idempotencyKey: reason, messageUid: reason })
        }, reason)
        assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'waiting reason does not establish Idle')
        results.push({ scenario: `waiting-${reason}-actual-active`, legacy, passed: true })
        await page.evaluate(() => runtimeTest.event('Idle'))
        await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      }
      for (const newer of ['Active', 'stream', 'replacement']) {
        await page.evaluate(() => {
          runtimeTest.status = 'Idle'; runtimeTest.holdAdmission = true
          runtimeTest.admission = { accepted: true, disposition: 'queued', sessionId: 'session-live', queue: { state: 'waiting_for_session', blockedReason: 'maintenance_drain' } }
          runtimeTest.runtime.sendMessage('live', 'delayed fixture', undefined, { idempotencyKey: 'delayed', messageUid: 'delayed-uid' })
        })
        await page.waitForFunction(() => runtimeTest.pendingAdmission.length === 1)
        await page.evaluate(newer => {
          if (newer === 'Active') runtimeTest.event('Active')
          else if (newer === 'replacement') runtimeTest.replace('replacement')
          else runtimeTest.stream({ type: 'text', content: 'Actual provider output' })
          runtimeTest.pendingAdmission.shift()(); runtimeTest.holdAdmission = false
        }, newer)
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))
        if (newer === 'replacement') assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.sessionId), 'replacement', 'delayed ack cannot replace the canonical session')
        else assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'delayed waiting ack cannot reverse newer work')
        results.push({ scenario: `queued-ack-after-new-${newer}`, legacy, passed: true })
        await page.evaluate(() => runtimeTest.replace('session-live'))
        await page.waitForFunction(() => runtimeTest.runtime.activeDiscussion.sessionId === 'session-live')
        await page.evaluate(() => { runtimeTest.event('Active'); runtimeTest.event('Idle') })
        await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      }
      for (const newer of ['Active', 'delivery', 'Starting', 'question', 'replacement']) {
        const reads = await page.evaluate(() => runtimeTest.authorityReads)
        await page.evaluate(() => {
          runtimeTest.status = 'Idle'; runtimeTest.holdAuthority = true
          return runtimeTest.runtime.sendMessage('live', 'held authority fixture', undefined, { idempotencyKey: 'held', messageUid: 'held-uid' })
        })
        await page.waitForFunction(() => runtimeTest.pendingAuthority.length === 1)
        await page.evaluate(async newer => {
          if (newer === 'delivery') {
            runtimeTest.admission = { accepted: true, disposition: 'delivered' }
            await runtimeTest.runtime.sendMessage('live', 'delivered fixture', undefined, { idempotencyKey: 'delivered', messageUid: 'delivered-uid' })
          } else if (newer === 'question') runtimeTest.stream({ type: 'question', requestId: 'admission-question', toolInput: { questions: [{ question: 'Choose', options: [] }] } })
          else if (newer === 'replacement') runtimeTest.replace('replacement')
          else runtimeTest.event(newer)
          runtimeTest.pendingAuthority.shift()(); runtimeTest.holdAuthority = false
        }, newer)
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))
        if (newer === 'question') assert.equal(await page.evaluate(() => !!runtimeTest.runtime.pendingQuestion), true)
        else if (newer === 'Starting') assert.equal(await page.evaluate(() => runtimeTest.runtime.isResumePending), true)
        else if (newer === 'replacement') assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.sessionId), 'replacement')
        else assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'stale Idle admission read cannot reverse newer work/delivery')
        assert.equal(await page.evaluate(() => runtimeTest.authorityReads), reads + 1, 'one bounded lifecycle read, no polling')
        results.push({ scenario: `waiting-authority-read-after-new-${newer}`, legacy, passed: true })
        await page.evaluate(() => runtimeTest.replace('session-live'))
        await page.waitForFunction(() => runtimeTest.runtime.activeDiscussion.sessionId === 'session-live')
        await page.evaluate(() => {
          runtimeTest.stream({ type: 'status', content: 'killed' }); runtimeTest.stream({ type: 'status', content: 'idle' })
          runtimeTest.event('Active'); runtimeTest.event('Idle')
          runtimeTest.admission = { accepted: true, disposition: 'queued', queue: { state: 'waiting_for_session', blockedReason: 'maintenance_drain' } }
        })
        await page.waitForFunction(() => !runtimeTest.runtime.isStreaming && !runtimeTest.runtime.isResumePending && !runtimeTest.runtime.pendingQuestion)
      }
      await page.evaluate(() => {
        runtimeTest.unavailableAuthority = true
        return runtimeTest.runtime.sendMessage('live', 'unavailable fixture', undefined, { idempotencyKey: 'unavailable', messageUid: 'unavailable-uid' })
      })
      await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))))
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'unavailable evidence cannot establish Idle')
      results.push({ scenario: 'unavailable-authority-retains-grace', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.unavailableAuthority = false; runtimeTest.event('Active'); runtimeTest.event('Idle') })
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      await page.evaluate(() => { runtimeTest.admission = { accepted: true, disposition: 'queued' } })
      await page.evaluate(() => runtimeTest.event('Idle'))
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      await page.evaluate(() => runtimeTest.runtime.sendMessage('live', 'optimistic fixture', undefined, { idempotencyKey: 'client', messageUid: 'uid' }))
      await page.evaluate(() => { runtimeTest.status = 'Idle'; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages)
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), true, 'pre-Active send grace retained')
      await page.evaluate(() => runtimeTest.event('Active'))
      await page.evaluate(() => runtimeTest.event('Idle'))
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming)
      results.push({ scenario: 'pre-active-grace-and-observed-settlement', legacy, passed: true })
      await page.evaluate(() => runtimeTest.event('Starting'))
      await page.waitForFunction(() => runtimeTest.runtime.isResumePending)
      await page.evaluate(() => runtimeTest.event('Active'))
      await page.waitForFunction(() => !runtimeTest.runtime.isResumePending && runtimeTest.runtime.isStreaming)
      results.push({ scenario: 'starting-to-active', legacy, passed: true })
      await page.evaluate(() => runtimeTest.stream({ type: 'question', requestId: 'question', toolInput: { questions: [{ question: 'Choose', options: [] }] } }))
      await page.waitForFunction(() => runtimeTest.runtime.pendingQuestion && !runtimeTest.runtime.isStreaming)
      await page.evaluate(() => runtimeTest.event('Active'))
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false, 'Active cannot hide a pending question')
      await page.evaluate(() => { runtimeTest.status = 'Active'; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages)
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false, 'Active snapshot preserves question veto')
      results.push({ scenario: 'pending-question-veto', legacy, passed: true })
      await page.evaluate(() => runtimeTest.stream({ type: 'status', content: 'killed' }))
      await page.waitForFunction(() => runtimeTest.runtime.isResumePending)
      await page.evaluate(() => runtimeTest.stream({ type: 'status', content: 'idle' }))
      await page.waitForFunction(() => !runtimeTest.runtime.isResumePending)
      results.push({ scenario: 'killed-resume-pending', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.status = 'Active'; runtimeTest.hold = true; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => runtimeTest.pending.length > 0)
      await page.evaluate(() => {
        runtimeTest.status = 'Idle'
        runtimeTest.runtime.handleWsEvent({ type: 'discussion.cleared', data: { discussionId: 'live' } })
        runtimeTest.hold = false; runtimeTest.pending.shift()()
      })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages)
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false)
      results.push({ scenario: 'clear-invalidates-old-active-read', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.status = 'Active'; runtimeTest.hold = true; runtimeTest.runtime.reloadActiveMessages(true) })
      await page.waitForFunction(() => runtimeTest.pending.length > 0)
      await page.evaluate(() => { runtimeTest.replace('replacement'); runtimeTest.pending.shift()(); runtimeTest.hold = false })
      await page.waitForFunction(() => !runtimeTest.runtime.isLoadingMessages)
      assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.sessionId), 'replacement')
      await page.evaluate(() => runtimeTest.runtime.renameDiscussion('live', 'replacement metadata fixture'))
      assert.equal(await page.evaluate(() => runtimeTest.runtime.activeDiscussion.sessionId), 'replacement', 'metadata ack cannot replace a newer session')
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false)
      results.push({ scenario: 'session-replacement-rejects-old-read', legacy, passed: true })
      await page.evaluate(() => runtimeTest.replace('session-live'))
      await page.waitForFunction(() => runtimeTest.runtime.activeDiscussion.sessionId === 'session-live')
      await page.evaluate(() => runtimeTest.event('Active'))
      await page.waitForFunction(() => runtimeTest.runtime.isStreaming)
      await page.evaluate(() => { runtimeTest.listStatus = 'archived'; return runtimeTest.runtime.refreshDiscussions() })
      await page.waitForFunction(() => !runtimeTest.runtime.isStreaming && runtimeTest.runtime.activeDiscussion.status === 'archived')
      results.push({ scenario: 'authoritative-closed-snapshot', legacy, passed: true })
      await page.evaluate(() => { runtimeTest.event('Active'); runtimeTest.stream({ type: 'text', content: 'stale' }) })
      assert.equal(await page.evaluate(() => runtimeTest.runtime.isStreaming), false)
      results.push({ scenario: 'closed-lifecycle', legacy, passed: true })
      await page.evaluate(() => runtimeTest.runtime.handleWsEvent({ type: 'discussion.rotated', data: { oldDiscussionId: 'live', newDiscussionId: 'rotated', agentId: 'agent' } }))
      await page.evaluate(() => runtimeTest.runtime.refreshDiscussions())
      assert.equal(await page.evaluate(() => runtimeTest.runtime.discussions.some(d => d.id === 'live')), false, 'stale list cannot resurrect rotated discussion')
      results.push({ scenario: 'rotation-tombstone', legacy, passed: true })
      assert.deepEqual(errors, [])
      const knownUpdaterWarning = consoleErrors.filter(e => e.includes('Cannot update a component') && e.includes('while rendering a different component'))
      assert.deepEqual(consoleErrors.filter(e => !knownUpdaterWarning.includes(e)), [])
      observations.knownUpdaterWarning = knownUpdaterWarning
      await page.close()
    }
  } finally { await browser?.close(); await server.close(); fs.writeFileSync(path.join(scratch, 'runtime-results.json'), JSON.stringify(results, null, 2)) }
  console.log(`${results.filter(result => result.passed).length} mounted runtime cases passed`)
}
main().catch(e => { console.error(e); process.exitCode = 1 })
