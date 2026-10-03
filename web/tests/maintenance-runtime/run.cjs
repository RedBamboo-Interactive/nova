const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path')
const { pathToFileURL } = require('node:url')
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const nova = path.resolve(__dirname, '../..')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))
const snapshot = (state = 'draining', count = 2) => ({ state, paused: ['draining', 'launching'].includes(state), runId: 'test-run', startedAt: '2026-10-03T10:00:00Z', changedAt: '2026-10-03T10:00:01Z', activeTurnCount: count })
async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error('REDLEAF_SCRATCH_DIR required')
  const { createServer } = await import(pathToFileURL(require.resolve('vite', { paths: [nova] })))
  const server = await createServer({ configFile: false, root: __dirname, cacheDir: path.join(scratch, 'maintenance-vite-cache'),
    resolve: { alias: { react: path.join(nova, 'node_modules/react'), 'react-dom': path.join(nova, 'node_modules/react-dom') } },
    server: { host: '127.0.0.1', port: 0, fs: { allow: [path.resolve(nova, '..'), shared, scratch] } } })
  let browser; let cases = 0
  try {
    await server.listen(); browser = await chromium.launch({ headless: true })
    for (const width of [1440, 390]) for (const newChat of [false, true]) {
      const page = await browser.newPage({ viewport: { width, height: 850 } })
      const errors = []; page.on('pageerror', e => errors.push(e.message))
      await page.goto(server.resolvedUrls.local[0] + (newChat ? '?new' : ''))
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      await page.evaluate(s => maintenanceTest.pending.shift().resolve(s), snapshot())
      await page.getByText('Update pending.', { exact: true }).waitFor()
      await page.getByText(/Waiting for 2 active conversations/).waitFor()
      assert((await page.locator('[data-slot="maintenance-notice"]').textContent()).includes(newChat ? 'Your draft stays on this device' : 'Accepted messages are saved'))
      await page.screenshot({ path: path.join(scratch, `maintenance-paused-${width}-${newChat}.png`) })
      await page.evaluate(() => maintenanceTest.refresh())
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      // Disconnect invalidates an already outstanding response. It cannot clear the known pause.
      await page.evaluate(s => { maintenanceTest.event('upstream.disconnected'); maintenanceTest.pending.shift().resolve(s) }, snapshot('idle', null))
      await page.getByText('Update status unavailable.', { exact: true }).waitFor()
      assert.equal(await page.evaluate(() => maintenanceTest.value.status.state), 'draining')
      await page.evaluate(() => maintenanceTest.event('upstream.connected'))
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      await page.evaluate(s => maintenanceTest.pending.shift().resolve(s), snapshot('launching', 0))
      await page.getByText('Restarting for an update.', { exact: true }).waitFor()
      // A burst coalesces to one trailing read, and the older read is discarded.
      await page.evaluate(() => { maintenanceTest.refresh(); maintenanceTest.refresh(); maintenanceTest.refresh() })
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      await page.evaluate(s => maintenanceTest.pending.shift().resolve(s), snapshot('draining', 9))
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      assert.equal(await page.evaluate(() => maintenanceTest.value.status.state), 'launching')
      await page.evaluate(s => maintenanceTest.pending.shift().resolve(s), snapshot('failed', null))
      await page.getByText('Update could not finish.', { exact: true }).waitFor()
      await page.getByText(/Message delivery has resumed/).waitFor()
      await page.evaluate(() => window.dispatchEvent(new Event('focus')))
      await page.waitForFunction(() => maintenanceTest.pending.length === 1)
      await page.evaluate(s => maintenanceTest.pending.shift().resolve(s), snapshot('idle', null))
      await page.locator('[data-slot="maintenance-notice"]').waitFor({ state: 'hidden' })
      await page.screenshot({ path: path.join(scratch, `maintenance-restored-${width}-${newChat}.png`) })
      assert.deepEqual(errors, []); cases++; await page.close()
    }
  } finally { await browser?.close(); await server.close() }
  fs.writeFileSync(path.join(scratch, 'maintenance-ui-results.json'), JSON.stringify({ cases, passed: true }))
  console.log(`${cases} mounted maintenance transition cases passed`)
}
main().catch(e => { console.error(e); process.exitCode = 1 })
