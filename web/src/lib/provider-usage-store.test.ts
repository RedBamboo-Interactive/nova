import assert from "node:assert/strict"
import test from "node:test"
import type { ProviderUsageSnapshot, ProviderUsageWindow } from "@redbamboo/chat"
import {
  ProviderUsageStore,
  quotaRingLabel,
  quotaToneColor,
  recordingUsageLoader,
  resolveLiveProvider,
  summarizeProviderQuota,
  usageKey,
  type QuotaFormat,
} from "./provider-usage-store.ts"

// Same rules as @redbamboo/chat's formatProviderUsageWindow / remainingProviderUsage.
const format: QuotaFormat = {
  window: minutes => minutes === 60 * 24 * 7 ? "Weekly" : minutes % 60 === 0 ? `${minutes / 60} hours` : `${minutes} minutes`,
  remaining: (window: ProviderUsageWindow) => Math.max(0, Math.round((100 - window.usedPercent) * 10) / 10),
}

// The live snapshots read on 2026-10-30 through /ai-session/providers/{provider}/usage.
const claude: ProviderUsageSnapshot = {
  provider: "claude-code",
  displayName: "Claude Code",
  fetchedAt: "2026-10-30T08:52:39Z",
  buckets: [{
    id: "claude",
    name: "Claude",
    windows: [
      { id: "five_hour", usedPercent: 5, windowDurationMinutes: 300, resetsAt: "2026-10-30T10:50:00Z" },
      { id: "seven_day", usedPercent: 93, windowDurationMinutes: 10080, resetsAt: "2026-10-30T20:00:00Z" },
    ],
  }],
}
const codex: ProviderUsageSnapshot = {
  provider: "codex",
  displayName: "Codex",
  planType: "pro",
  fetchedAt: "2026-10-30T08:53:07Z",
  buckets: [{ id: "codex", name: "Codex", windows: [{ id: "primary", usedPercent: 3, windowDurationMinutes: 10080, resetsAt: "2026-11-04T06:44:34Z" }] }],
}
const providers = [
  { slug: "opencode-default", backend: "opencode" },
  { slug: "codex-default", backend: "codex" },
  { slug: "anthropic-direct", backend: "claude-code" },
  { slug: "ollama-8768777b", backend: "opencode" },
  { slug: "muse-spark-b8f5a973", backend: "opencode" },
]

function clock(start = 1_000_000) {
  let now = start
  return { now: () => now, advance(ms: number) { now += ms } }
}

test("the ring shows the most constrained window, not the friendliest one", () => {
  const summary = summarizeProviderQuota(claude, format)
  assert.deepEqual(summary && {
    remaining: summary.remainingPercent,
    used: summary.usedPercent,
    window: summary.windowLabel,
  }, { remaining: 7, used: 93, window: "Weekly" })
  assert.equal(quotaRingLabel(summary!), "Claude Code quota: 7% left (Weekly window)")
})

test("no ring when quota is unknown", () => {
  assert.equal(summarizeProviderQuota(undefined, format), null, "never read")
  assert.equal(summarizeProviderQuota(null, format), null, "unsupported or failed")
  assert.equal(summarizeProviderQuota({ ...codex, buckets: [] }, format), null, "no windows")
  assert.equal(summarizeProviderQuota({ ...codex, buckets: [{ id: "x", name: "x", windows: [] }] }, format), null)
})

test("ring tone follows the Session Info thresholds", () => {
  assert.equal(quotaToneColor(3), "var(--color-accent-teal)")
  assert.equal(quotaToneColor(93), "var(--color-accent-gold)")
  assert.equal(quotaToneColor(100), "var(--color-accent-red)")
})

test("a provider slug and its session backend id share one cache entry", () => {
  assert.equal(usageKey("anthropic-direct", providers), "claude-code")
  assert.equal(usageKey("codex-default", providers), "codex")
  assert.equal(usageKey("claude-code", providers), "claude-code")
  // Three slugs run on OpenCode with different accounts: never collapse them.
  assert.equal(usageKey("ollama-8768777b", providers), "ollama-8768777b")
  assert.equal(usageKey("unknown-provider", providers), "unknown-provider")
})

test("the Live provider is its session's, then its override, then its Agent's", () => {
  assert.equal(resolveLiveProvider({ provider: null }, "anthropic-direct"), "anthropic-direct")
  assert.equal(resolveLiveProvider({ provider: "codex-default" }, "anthropic-direct"), "codex-default")
  assert.equal(resolveLiveProvider({ provider: null }, "anthropic-direct", "claude-code"), "claude-code")
  assert.equal(resolveLiveProvider({ provider: null }, null), null)
})

test("Session Info reads feed the ring; the ring then adds no provider call", async () => {
  const time = clock()
  const store = new ProviderUsageStore(time.now)
  let calls = 0
  const load = async (provider: string, force?: boolean) => {
    calls++
    assert.equal(provider, "claude-code")
    void force
    return claude
  }
  const sessionInfoLoader = recordingUsageLoader(store, load)

  await sessionInfoLoader("claude-code", true) // Session Info opened (forced)
  assert.equal(calls, 1)
  await store.ensure(usageKey("anthropic-direct", providers), load) // ring for the Live
  for (let render = 0; render < 50; render++) store.get("claude-code") // renders only read the cache
  assert.equal(calls, 1)
  assert.equal(summarizeProviderQuota(store.get("claude-code"), format)?.remainingPercent, 7)
})

test("ring reads are single-flight, never forced, and gated by freshness", async () => {
  const time = clock()
  const store = new ProviderUsageStore(time.now)
  const forced: Array<boolean | undefined> = []
  let release!: () => void
  const load = async (_provider: string, force?: boolean) => {
    forced.push(force)
    await new Promise<void>(resolve => { release = resolve })
    return codex
  }

  const a = store.ensure("codex", load)
  const b = store.ensure("codex", load)
  release()
  await Promise.all([a, b])
  assert.deepEqual(forced, [false])

  time.advance(30_000)
  await store.ensure("codex", load)
  assert.equal(forced.length, 1, "still fresh")

  time.advance(31_000)
  const c = store.ensure("codex", load)
  release()
  await c
  assert.deepEqual(forced, [false, false])
})

test("Session Info's mount read and the ring's first read share one request", async () => {
  const store = new ProviderUsageStore()
  let calls = 0
  let release!: () => void
  const load = async () => {
    calls++
    await new Promise<void>(resolve => { release = resolve })
    return claude
  }
  const sessionInfo = recordingUsageLoader(store, load, id => usageKey(id, providers))
  const info = sessionInfo("claude-code", false)
  const ring = store.ensure(usageKey("anthropic-direct", providers), load)
  release()
  assert.equal(await info, claude)
  await ring
  assert.equal(calls, 1)

  // Opening Session Info still forces a fresh provider read.
  const forced = sessionInfo("claude-code", true)
  release()
  await forced
  assert.equal(calls, 2)
})

test("a Session Info read just after the ring's is answered from it; later turns read again", async () => {
  const time = clock()
  const store = new ProviderUsageStore(time.now)
  let calls = 0
  const load = async () => { calls++; return claude }
  const sessionInfo = recordingUsageLoader(store, load, id => usageKey(id, providers))

  await store.ensure(usageKey("anthropic-direct", providers), load) // ring mounts first
  time.advance(400)
  assert.equal(await sessionInfo("claude-code", false), claude) // Session Info mounts
  assert.equal(calls, 1)

  time.advance(20_000) // a later turn settles: Session Info's own cadence still reads
  await sessionInfo("claude-code", false)
  assert.equal(calls, 2)
})

test("Session Info naming a slug records under the canonical key the ring reads", async () => {
  const store = new ProviderUsageStore()
  const loader = recordingUsageLoader(store, async () => codex, id => usageKey(id, providers))
  await loader("codex-default", false)
  assert.equal(store.get("codex"), codex)
  assert.equal(store.get("codex-default"), undefined)
})

test("unsupported or failing providers are remembered as unknown, without throwing", async () => {
  const time = clock()
  const store = new ProviderUsageStore(time.now)
  let calls = 0
  const notSupported = async () => { calls++; throw Object.assign(new Error("usage_not_supported"), { status: 404 }) }
  await store.ensure("ollama-8768777b", notSupported)
  await store.ensure("ollama-8768777b", notSupported)
  assert.equal(calls, 1)
  assert.equal(store.get("ollama-8768777b"), null)
  assert.equal(store.get("never-read"), undefined)
})

test("Session Info still sees a loader failure, and the ring forgets the last value", async () => {
  const time = clock()
  const store = new ProviderUsageStore(time.now)
  store.record("codex", codex)
  time.advance(10_000)
  const loader = recordingUsageLoader(store, async () => { throw new Error("usage_unavailable") })
  await assert.rejects(loader("codex", false))
  assert.equal(store.get("codex"), null)
})

test("subscribers hear every recorded reading", () => {
  const store = new ProviderUsageStore()
  let heard = 0
  const off = store.subscribe(() => { heard++ })
  const before = store.getVersion()
  store.record("codex", codex)
  off()
  store.record("codex", null)
  assert.equal(heard, 1)
  assert.equal(store.getVersion(), before + 2)
})
