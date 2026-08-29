/**
 * Runs one task per key at a time while retaining the newest request that
 * arrived during the active task. Intermediate requests are superseded.
 *
 * Snapshot recovery uses this to avoid turning a burst of gap notifications
 * into a burst of overlapping history reads. Keeping one trailing task means
 * an accepted message or stronger revalidation request that raced the first
 * snapshot is still observed once that snapshot settles.
 */
export class LatestTaskCoordinator<Key> {
  private readonly states = new Map<Key, {
    active: Promise<void>
    pending?: () => Promise<void>
  }>()

  run(key: Key, task: () => Promise<void>, retainAsTrailing = true): Promise<void> {
    const existing = this.states.get(key)
    if (existing) {
      if (retainAsTrailing) existing.pending = task
      return existing.active
    }

    const state: {
      active: Promise<void>
      pending?: () => Promise<void>
    } = { active: Promise.resolve() }

    state.active = Promise.resolve().then(async () => {
      let next: (() => Promise<void>) | undefined = task
      let firstFailure: unknown
      while (next) {
        try {
          await next()
        } catch (error) {
          firstFailure ??= error
        }
        next = state.pending
        state.pending = undefined
      }
      if (firstFailure !== undefined) throw firstFailure
    }).finally(() => {
      if (this.states.get(key) === state) this.states.delete(key)
    })

    this.states.set(key, state)
    return state.active
  }
}
