/** A bounded search backwards through history for the message an artifact came from. */
export const GO_TO_MESSAGE_MAX_PAGES = 25

/** How long the controller lets a finished page commit to the view before it looks again. */
const SETTLE_MS = 50

export interface GoToMessageView {
  /** Whether the transcript still has pages before the oldest loaded one. */
  hasEarlier(): boolean
  /** True while an earlier page is being read, whoever asked for it. */
  isLoadingEarlier(): boolean
  /** Reads one earlier page through the same pager that scrolling up uses. */
  loadEarlier(): Promise<void> | void
  /** Asks the chat to centre the message; its answer arrives through `result`. */
  focus(request: { uid: string; nonce: number }): void
  /** Called once when the message could not be reached, after the bounded search. */
  missing(): void
  /** Called whenever the search starts or ends, for the visible "Loading earlier messages" state. */
  seeking(active: boolean): void
}

interface Search {
  uid: string
  pages: number
}

/**
 * Go to a message that may be outside the rendered window or the loaded pages: ask the chat to
 * centre it, and while the chat answers "not loaded" read one earlier page and ask again, at most
 * `maxPages` times. Framework-free so the loop can be exercised deterministically.
 */
export class GoToMessageController {
  private nonce = 0
  private search: Search | null = null
  private timer: ReturnType<typeof setTimeout> | null = null

  private readonly view: GoToMessageView
  private readonly maxPages: number
  private readonly schedule: (callback: () => void, ms: number) => ReturnType<typeof setTimeout>
  private readonly unschedule: (handle: ReturnType<typeof setTimeout>) => void

  constructor(
    view: GoToMessageView,
    maxPages: number = GO_TO_MESSAGE_MAX_PAGES,
    schedule: (callback: () => void, ms: number) => ReturnType<typeof setTimeout> = (callback, ms) => setTimeout(callback, ms),
    unschedule: (handle: ReturnType<typeof setTimeout>) => void = handle => clearTimeout(handle),
  ) {
    this.view = view
    this.maxPages = maxPages
    this.schedule = schedule
    this.unschedule = unschedule
  }

  /** The nonce of the request the chat is currently expected to answer. */
  get currentNonce(): number {
    return this.nonce
  }

  get active(): boolean {
    return this.search !== null
  }

  go(uid: string): void {
    this.stopTimer()
    this.search = { uid, pages: 0 }
    this.ask()
  }

  /** The chat's answer to the latest request. Stale answers are ignored. */
  result(answer: { uid: string; nonce: number; found: boolean }): void {
    const search = this.search
    if (!search || answer.nonce !== this.nonce || answer.uid !== search.uid) return
    if (answer.found) {
      this.finish()
      return
    }
    this.readEarlier(search)
  }

  /** The conversation changed or the view went away: nothing to find any more. */
  cancel(): void {
    this.stopTimer()
    if (this.search) this.finish()
  }

  private ask(): void {
    const search = this.search
    if (!search) return
    this.nonce += 1
    this.view.focus({ uid: search.uid, nonce: this.nonce })
  }

  private readEarlier(search: Search): void {
    if (search.pages >= this.maxPages || !this.view.hasEarlier()) {
      this.finish()
      this.view.missing()
      return
    }
    search.pages += 1
    this.view.seeking(true)
    // The pager refuses a second read while one is running, so a refused read costs nothing:
    // `resume` waits for the running read instead of counting another page.
    Promise.resolve(this.view.loadEarlier()).catch(() => {}).then(() => this.resumeSoon(search))
  }

  private resumeSoon(search: Search): void {
    this.stopTimer()
    this.timer = this.schedule(() => {
      this.timer = null
      if (this.search !== search) return
      if (this.view.isLoadingEarlier()) {
        this.resumeSoon(search)
        return
      }
      this.ask()
    }, SETTLE_MS)
  }

  private stopTimer(): void {
    if (this.timer !== null) this.unschedule(this.timer)
    this.timer = null
  }

  private finish(): void {
    this.search = null
    this.view.seeking(false)
  }
}
