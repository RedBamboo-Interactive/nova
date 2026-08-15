import { useCallback, useEffect, useMemo, useState } from "react"
import { PluginExtensionSlot } from "@redbamboo/utility"

type AvatarVariant = "mobile" | "sidebar"

interface AvatarVisual {
  identity: string
  src: string
  agentId: string | null
  discussionId: string | null
}

interface AvatarLayers {
  current: AvatarVisual
  incoming: AvatarVisual | null
}

interface TransitioningAgentAvatarProps {
  src: string
  agentId: string | null
  discussionId: string | null
  variant: AvatarVariant
  imageOpacity?: number
  className?: string
}

function sameContext(left: AvatarVisual, right: AvatarVisual) {
  return left.agentId === right.agentId && left.discussionId === right.discussionId
}

function AvatarLayer({
  visual,
  variant,
  imageOpacity,
  incoming,
}: {
  visual: AvatarVisual
  variant: AvatarVariant
  imageOpacity: number
  incoming: boolean
}) {
  return (
    <div
      className={`nova-agent-avatar-layer${incoming ? " nova-agent-avatar-layer--incoming" : ""}`}
      data-avatar-agent-id={visual.agentId ?? undefined}
    >
      <img
        src={visual.src}
        alt=""
        draggable={false}
        className="size-full rounded-full object-cover object-top"
        style={{ opacity: imageOpacity }}
      />
      <PluginExtensionSlot
        targetPluginId="nova"
        slotId="chat-avatar-overlay"
        context={{
          agentId: visual.agentId,
          discussionId: visual.discussionId,
          variant,
        }}
      />
    </div>
  )
}

export function TransitioningAgentAvatar({
  src,
  agentId,
  discussionId,
  variant,
  imageOpacity = 1,
  className = "",
}: TransitioningAgentAvatarProps) {
  const desired = useMemo<AvatarVisual>(() => ({
    identity: `${agentId ?? "default"}\u0000${src}`,
    src,
    agentId,
    discussionId,
  }), [agentId, discussionId, src])
  const [layers, setLayers] = useState<AvatarLayers>(() => ({ current: desired, incoming: null }))

  const finishReveal = useCallback(() => {
    setLayers(previous => previous.incoming
      ? { current: previous.incoming, incoming: null }
      : previous)
  }, [])

  useEffect(() => {
    if (layers.incoming) {
      if (layers.incoming.identity === desired.identity && !sameContext(layers.incoming, desired)) {
        setLayers(previous => previous.incoming?.identity === desired.identity
          ? { ...previous, incoming: desired }
          : previous)
      }
      return
    }

    if (layers.current.identity === desired.identity) {
      if (!sameContext(layers.current, desired)) {
        setLayers(previous => previous.current.identity === desired.identity
          ? { ...previous, current: desired }
          : previous)
      }
      return
    }

    let cancelled = false
    let settled = false
    const image = new Image()

    const showDesired = async () => {
      if (settled) return
      settled = true
      try {
        await image.decode()
      } catch {
        // A successful load is enough. Some browsers reject decode() for an
        // image they have already decoded and can paint.
      }
      if (cancelled) return

      if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
        setLayers({ current: desired, incoming: null })
      } else {
        setLayers(previous => previous.incoming
          ? previous
          : { ...previous, incoming: desired })
      }
    }

    const showBrokenDesired = () => {
      if (settled || cancelled) return
      settled = true
      // Do not leave the previous agent on screen indefinitely if the new
      // asset fails. The browser's broken-image state is honest and retryable.
      setLayers({ current: desired, incoming: null })
    }

    image.addEventListener("load", showDesired, { once: true })
    image.addEventListener("error", showBrokenDesired, { once: true })
    image.src = desired.src
    if (image.complete) {
      if (image.naturalWidth > 0) void showDesired()
      else showBrokenDesired()
    }

    return () => {
      cancelled = true
      image.removeEventListener("load", showDesired)
      image.removeEventListener("error", showBrokenDesired)
    }
  }, [desired, layers.current, layers.incoming])

  useEffect(() => {
    if (!layers.incoming) return
    // animationend is the normal path. The timer is a guard for backgrounded
    // tabs and host styles that suppress CSS animation events.
    const timeout = window.setTimeout(finishReveal, 800)
    return () => window.clearTimeout(timeout)
  }, [finishReveal, layers.incoming])

  return (
    <div
      role="presentation"
      aria-hidden="true"
      className={`relative size-full overflow-hidden rounded-full${layers.incoming ? " nova-agent-avatar--transitioning" : ""} ${className}`}
      data-slot="transitioning-agent-avatar"
      data-avatar-agent-id={desired.agentId ?? undefined}
      data-avatar-transitioning={layers.incoming ? "true" : "false"}
      onAnimationEnd={event => {
        if (event.target === event.currentTarget && event.animationName === "nova-avatar-transition-clock") {
          finishReveal()
        }
      }}
    >
      <AvatarLayer
        visual={layers.current}
        variant={variant}
        imageOpacity={imageOpacity}
        incoming={false}
      />
      {layers.incoming && (
        <AvatarLayer
          visual={layers.incoming}
          variant={variant}
          imageOpacity={imageOpacity}
          incoming
        />
      )}
    </div>
  )
}
