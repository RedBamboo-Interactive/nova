const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path')
const { pathToFileURL } = require('node:url')
const shared = path.resolve(__dirname, '../../../../redbamboo-packages')
const { chromium } = require(require.resolve('playwright', { paths: [path.join(shared, 'packages/testing')] }))

// Mounted sidebar: each row shows one indicator in its avatar, chosen by the
// chat activity row's priority, and nothing beside the timestamp.
async function main() {
  const scratch = process.env.REDLEAF_SCRATCH_DIR
  if (!scratch) throw new Error('REDLEAF_SCRATCH_DIR required')
  const shots = path.join(scratch, 'sidebar-activity')
  fs.mkdirSync(shots, { recursive: true })
  const nova = path.resolve(__dirname, '../..')
  const { createServer } = await import(pathToFileURL(require.resolve('vite', { paths: [nova] })))
  const tailwind = (await import(pathToFileURL(require.resolve('@tailwindcss/vite', { paths: [path.join(shared, 'packages/ui')] })))).default
  const server = await createServer({ configFile: false, root: __dirname, plugins: [tailwind()], logLevel: 'error',
    cacheDir: path.join(scratch, 'sidebar-vite-cache'),
    resolve: { alias: { react: path.join(nova, 'node_modules/react'), 'react-dom': path.join(nova, 'node_modules/react-dom') } },
    server: { host: '127.0.0.1', port: 0, fs: { allow: [path.resolve(nova, '..'), shared, scratch] } } })
  let browser
  try {
    await server.listen(); browser = await chromium.launch({ headless: true })
    const page = await browser.newPage({ viewport: { width: 420, height: 420 }, deviceScaleFactor: 2 })
    const errors = []
    page.on('pageerror', e => errors.push(e.message))
    page.on('console', e => { if (e.type() === 'error') errors.push(e.text()) })
    await page.goto(server.resolvedUrls.local[0])
    const row = id => page.locator(`[data-discussion-id="${id}"]`)
    await row('idle').waitFor()
    const indicator = id => row(id).locator('[data-slot="discussion-activity"]')
    // The session working outranks the agent behind it: the ordinary thinking spinner.
    assert.equal(await indicator('session').count(), 0, 'the session owns its indicator while it works')
    assert.equal(await indicator('agents').getAttribute('data-activity-source'), 'agents')
    assert.equal(await indicator('agents').getAttribute('aria-label'), '2 agents working')
    assert.equal(await indicator('tasks').getAttribute('data-activity-source'), 'tasks')
    assert.equal(await indicator('tasks').getAttribute('aria-label'), '1 shell working')
    assert.equal(await indicator('delegations').getAttribute('data-activity-source'), 'delegations')
    assert.equal(await indicator('idle').count(), 0)
    // One indicator per row: nothing beside the timestamp any more.
    for (const id of ['session', 'agents', 'tasks', 'delegations', 'idle']) {
      assert.equal(await row(id).locator('[data-slot="item-list-icon"] ~ * [data-slot="delegation-spinner"], [data-slot="background-work-count"]').count(), 0, `${id} has no subtitle spinners`)
    }
    await page.evaluate(() => new Promise(r => setTimeout(r, 400)))
    await page.screenshot({ path: path.join(shots, 'sidebar-activity.png') })
    // Known, separate defect: the row actions trigger is a button inside the row button.
    const nestedButton = error => /cannot contain a nested|cannot be a descendant of/.test(error) && error.includes('button')
    assert.deepEqual(errors.filter(error => !nestedButton(error)), [])
    console.log(`sidebar activity checks passed; screenshot in ${shots}`)
  } finally {
    await browser?.close(); await server.close()
  }
}
main().catch(error => { console.error(error); process.exitCode = 1 })
