# Nova immersive voice mode

## Product contract

Voice mode is a temporary live-audio presentation of the discussion that is
already open. It never creates a second Nova conversation, memory, transcript,
Agent identity, permission scope, or tool ledger.

The current discussion remains mounted beneath the immersive surface. Starting
voice mode creates one ephemeral provider session bound to that discussion;
ending it releases browser media immediately, requests the existing authorized
server-side close, lets the visual surface finish a short exit, and returns to
the unchanged discussion.

## Experience

The transition should feel like entering a call rather than opening a utility
panel:

- the active Agent portrait fills the viewport as a softly blurred, darkened
  background;
- a crisp central portrait and audio orb carry connecting, listening,
  speaking, working, muted, error, and ending states;
- live captions remain readable in a restrained glass surface;
- mute and end are the only controls, with safe-area spacing on phones;
- Escape ends the call, focus stays within the modal controls, and focus returns
  to the initiating control after the mode closes;
- reduced-motion users receive the same state changes without decorative
  scaling, drifting, or pulsing.

The visual speaking/listening state follows recent transcript delta activity.
That signal is deliberately cosmetic. It does not finalize utterances or alter
the canonical transcript boundaries owned by the voice-session coordinator.

## Lifecycle

```text
idle
  -> starting      immersive surface enters; microphone permission / SDP setup
  -> connected     live audio; visual state derives from recent caption deltas
  -> degraded      transport interrupted; controls remain available
  -> error         admission or browser-media failure; error stays visible
  -> closing       media stops immediately; surface performs bounded exit
  -> closed/idle   surface unmounts; existing discussion is still current
```

Delegated Nova work is a connected sub-state, not a second session. It takes
visual precedence over listening/speaking and returns to the live path when a
spoken result arrives.

## Acceptance

- No discussion, AI-session, message UID, delegation, or transcript contract
  changes.
- One provider admission per start gesture and at most one close request for the
  accepted session.
- Microphone tracks stop before the exit animation begins.
- Phone and desktop have no horizontal overflow and preserve safe-area controls.
- Opening and closing are smooth; reduced motion collapses decorative movement.
- The real product route has no page or console errors under a mocked media and
  provider boundary. A paid provider call is a separate acceptance gate.
