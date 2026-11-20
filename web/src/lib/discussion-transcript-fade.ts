const TRANSCRIPT_ROW = "[data-chat-entrance-row]"
const LIVE_MARKERS = ["id", "data-chat-entrance-row", "data-message-uid", "data-input-message-uid"]
const LIVE_MARKER_SELECTOR = LIVE_MARKERS.map(name => `[${name}]`).join(",")

/** Draw only what was visible, without keeping any outgoing chat controls alive. */
export function fadeOutgoingTranscript(pane: HTMLElement, layer: HTMLElement) {
  layer.replaceChildren()
  if (!pane.isConnected || !layer.animate) return

  // Shared chat marks each entrance row inside its content column and scroller.
  const content = pane.querySelector(TRANSCRIPT_ROW)?.parentElement
  const viewport = content?.parentElement
  if (!content || !viewport) return

  const bounds = viewport.getBoundingClientRect()
  const layerBounds = layer.getBoundingClientRect()
  if (!bounds.width || !bounds.height) return

  const snapshot = pane.ownerDocument.createElement("div")
  snapshot.className = "nova-transcript-exit"
  snapshot.style.left = `${bounds.left - layerBounds.left}px`
  snapshot.style.top = `${bounds.top - layerBounds.top}px`
  snapshot.style.width = `${viewport.clientWidth}px`
  snapshot.style.height = `${viewport.clientHeight}px`

  for (const row of content.children) {
    if (!row.matches(TRANSCRIPT_ROW)) continue
    const rect = row.getBoundingClientRect()
    if (rect.bottom <= bounds.top || rect.top >= bounds.bottom || !rect.width) continue

    const copy = row.cloneNode(true) as HTMLElement
    // Omit live media players and remove identities used by history and the browser.
    for (const media of copy.querySelectorAll("video, audio, iframe, object, embed")) media.remove()
    const identified = copy.querySelectorAll(LIVE_MARKER_SELECTOR)
    for (const element of [copy, ...identified]) {
      for (const name of LIVE_MARKERS) element.removeAttribute(name)
    }

    copy.style.position = "absolute"
    copy.style.margin = "0"
    copy.style.left = `${rect.left - bounds.left}px`
    copy.style.top = `${rect.top - bounds.top}px`
    copy.style.width = `${rect.width}px`
    snapshot.append(copy)
  }

  if (!snapshot.childElementCount) return
  layer.append(snapshot)
  const exit = snapshot.animate([{ opacity: 0.9 }, { opacity: 0 }], {
    duration: 140,
    easing: "ease-out",
    fill: "forwards",
  })
  void exit.finished.then(() => snapshot.remove(), () => snapshot.remove())
}
