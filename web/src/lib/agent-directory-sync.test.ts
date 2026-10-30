import assert from "node:assert/strict"
import test from "node:test"
import {
  AVATAR_CHANGED_EVENT,
  AgentDirectorySync,
  avatarsChanged,
  versionedAvatarSrc,
  type AgentDirectorySnapshot,
} from "./agent-directory-sync.ts"
import type { AgentInfo } from "./types.ts"

const NOVA = "a7b059ac-f678-8b58-819b-1bcf3acb22db"
const OLD_OUTFIT = "/api/assets/371c2a92-9773-4035-a4f4-bffcd5053842.png"
const NEW_OUTFIT = "/api/assets/b2750432-c2dc-4d8f-8627-c3325afe919b.png"

function agent(id: string, avatarUrl: string, slug = id): AgentInfo {
  return { id, slug, name: slug, description: null, avatarUrl }
}

/** The Agent entity on the server; the outfit can change while the phone sleeps. */
function server(initial: AgentInfo[]) {
  let agents = initial
  let reads = 0
  return {
    load: async () => { reads++; return agents.map(a => ({ ...a })) },
    set(next: AgentInfo[]) { agents = next },
    get reads() { return reads },
  }
}

function browser() {
  const window = new EventTarget()
  const document = Object.assign(new EventTarget(), { visibilityState: "visible" as DocumentVisibilityState })
  return {
    window,
    document,
    avatarEvents: window,
    hide() {
      document.visibilityState = "hidden"
      document.dispatchEvent(new Event("visibilitychange"))
    },
    show() {
      document.visibilityState = "visible"
      document.dispatchEvent(new Event("visibilitychange"))
    },
    focus() { window.dispatchEvent(new Event("focus")) },
  }
}

/** Mirrors WsDiscussionBridge: a frame reaches the page only while the socket is open. */
function socket(page: ReturnType<typeof browser>, sync: AgentDirectorySync) {
  let open = true
  let missed = 0
  return {
    drop() { open = false; },
    reconnect() { open = true; sync.handleWsEvent("websocket.connected") },
    emitAvatarChanged() {
      if (!open) { missed++; return }
      page.avatarEvents.dispatchEvent(new Event(AVATAR_CHANGED_EVENT))
    },
    get missed() { return missed },
  }
}

async function settle() {
  for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve))
}

function portraitSrc(snapshot: AgentDirectorySnapshot) {
  const nova = snapshot.agents.find(a => a.id === NOVA)
  return versionedAvatarSrc(nova ? nova.avatarUrl : "/api/apps/nova/avatar", snapshot.avatarRevision)
}

function mounted(initial: AgentInfo[]) {
  const backend = server(initial)
  const page = browser()
  const snapshots: AgentDirectorySnapshot[] = []
  const sync = new AgentDirectorySync(backend.load, snapshot => snapshots.push(snapshot))
  const detach = sync.attach(page)
  const ws = socket(page, sync)
  void sync.refresh()
  return { backend, page, sync, ws, detach, snapshots }
}

test("a missed avatar-changed frame across background and reconnect still shows the new outfit", async () => {
  const { backend, page, sync, ws } = mounted([agent(NOVA, OLD_OUTFIT, "nova")])
  await settle()
  assert.equal(portraitSrc(sync.snapshot), OLD_OUTFIT)

  // The phone sleeps: the page goes hidden and the OS drops the socket.
  page.hide()
  ws.drop()
  // Nova changes her outfit; the server emits agent.avatar-changed to nobody.
  backend.set([agent(NOVA, NEW_OUTFIT, "nova")])
  ws.emitAvatarChanged()
  await settle()
  assert.equal(ws.missed, 1)
  assert.equal(portraitSrc(sync.snapshot), OLD_OUTFIT, "the frame was missed, so nothing has changed yet")

  // Resume: Android emits visibility, focus and the fresh socket's connect together.
  page.show()
  page.focus()
  ws.reconnect()
  await settle()

  assert.equal(sync.snapshot.agents[0].avatarUrl, NEW_OUTFIT)
  assert.equal(portraitSrc(sync.snapshot), `${NEW_OUTFIT}?v=1`)
  assert.ok(backend.reads <= 3, `one resume burst coalesces (${backend.reads} reads including mount)`)
})

test("a reconnect alone recovers a missed frame (desktop network flap, page never hidden)", async () => {
  const { backend, sync, ws } = mounted([agent(NOVA, OLD_OUTFIT, "nova")])
  await settle()
  ws.drop()
  backend.set([agent(NOVA, NEW_OUTFIT, "nova")])
  ws.emitAvatarChanged()
  ws.reconnect()
  await settle()
  assert.equal(portraitSrc(sync.snapshot), `${NEW_OUTFIT}?v=1`)
})

test("a foreground return alone recovers a missed frame (socket survived but frame was lost)", async () => {
  const { backend, page, sync } = mounted([agent(NOVA, OLD_OUTFIT, "nova")])
  await settle()
  page.hide()
  backend.set([agent(NOVA, NEW_OUTFIT, "nova")])
  page.show()
  await settle()
  assert.equal(sync.snapshot.agents[0].avatarUrl, NEW_OUTFIT)
})

test("the live avatar-changed frame still refreshes immediately", async () => {
  const { backend, sync, ws } = mounted([agent(NOVA, OLD_OUTFIT, "nova")])
  await settle()
  backend.set([agent(NOVA, NEW_OUTFIT, "nova")])
  ws.emitAvatarChanged()
  await settle()
  assert.equal(portraitSrc(sync.snapshot), `${NEW_OUTFIT}?v=1`)
})

test("going hidden does not read, and an unchanged recovery read neither notifies nor re-keys", async () => {
  const { backend, page, sync, snapshots } = mounted([agent(NOVA, OLD_OUTFIT, "nova")])
  await settle()
  const readsAfterMount = backend.reads
  page.hide()
  await settle()
  assert.equal(backend.reads, readsAfterMount)

  page.show()
  await settle()
  assert.equal(backend.reads, readsAfterMount + 1)
  assert.equal(snapshots.length, 1, "identical snapshot does not re-render consumers")
  assert.equal(portraitSrc(sync.snapshot), OLD_OUTFIT, "no cache-busting request without a change")
})

test("overlapping recovery reads are serialized and the newest server state wins", async () => {
  let release!: () => void
  let reads = 0
  let serverAvatar = OLD_OUTFIT
  const sync = new AgentDirectorySync(async () => {
    reads++
    const seen = serverAvatar
    if (reads === 2) await new Promise<void>(resolve => { release = resolve })
    return [agent(NOVA, seen, "nova")]
  }, () => {})
  await sync.refresh()

  serverAvatar = NEW_OUTFIT
  const first = sync.refresh() // read 2 captures NEW, held open
  await settle()
  serverAvatar = "/api/assets/third.png"
  void sync.refresh()
  void sync.refresh()
  void sync.refresh() // the burst collapses into one trailing read
  release()
  await first
  await settle()

  assert.equal(reads, 3)
  assert.equal(sync.snapshot.agents[0].avatarUrl, "/api/assets/third.png")
  assert.equal(sync.snapshot.avatarRevision, 2)
})

test("a failed read keeps the last snapshot", async () => {
  let fail = false
  const sync = new AgentDirectorySync(async () => {
    if (fail) throw new Error("offline")
    return [agent(NOVA, OLD_OUTFIT, "nova")]
  }, () => {})
  await sync.refresh()
  fail = true
  await sync.refresh()
  assert.equal(sync.snapshot.agents[0].avatarUrl, OLD_OUTFIT)
})

test("after unmount a late read is ignored and listeners are gone", async () => {
  let release!: () => void
  let reads = 0
  const notified: AgentDirectorySnapshot[] = []
  const page = browser()
  const sync = new AgentDirectorySync(async () => {
    reads++
    await new Promise<void>(resolve => { release = resolve })
    return [agent(NOVA, NEW_OUTFIT, "nova")]
  }, snapshot => notified.push(snapshot))
  const detach = sync.attach(page)
  const pending = sync.refresh()
  await settle()
  detach()
  sync.dispose()
  release()
  await pending
  page.show()
  page.focus()
  page.avatarEvents.dispatchEvent(new Event(AVATAR_CHANGED_EVENT))
  sync.handleWsEvent("upstream.connected")
  await settle()
  assert.equal(reads, 1)
  assert.equal(notified.length, 0)
})

test("only an avatar URL change of a known Agent advances the revision", () => {
  const before = [agent(NOVA, OLD_OUTFIT, "nova"), agent("leni", "/api/assets/leni.png")]
  assert.equal(avatarsChanged(before, before.map(a => ({ ...a }))), false)
  assert.equal(avatarsChanged(before, [...before, agent("wren", "/api/assets/wren.png")]), false)
  assert.equal(avatarsChanged(before, [{ ...before[0], name: "Nova!" }, before[1]]), false)
  assert.equal(avatarsChanged(before, [agent(NOVA, NEW_OUTFIT, "nova"), before[1]]), true)
})

test("versioned avatar URLs keep existing query strings", () => {
  assert.equal(versionedAvatarSrc("/api/apps/nova/avatar", 0), "/api/apps/nova/avatar")
  assert.equal(versionedAvatarSrc("/api/apps/nova/avatar", 2), "/api/apps/nova/avatar?v=2")
  assert.equal(versionedAvatarSrc("/x.png?w=320", 1), "/x.png?w=320&v=1")
})
