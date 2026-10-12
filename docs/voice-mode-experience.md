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

GPT-Live owns the immediate low-latency spoken flow. It handles greetings,
acknowledgements and lightweight social conversation directly, and delegates when
the turn needs Nova's identity, durable memory, active work, reasoning, current
facts, tools, files, actions, commitments or safety decisions. Every direct or
delegated input and output is persisted into the ongoing canonical discussion with
signed provenance. If phone sleep or network loss requires a replacement transport,
admission waits for the prior session to flush and close before it snapshots that
complete discussion history.

At admission Nova derives a bounded voice persona from the selected Agent's trusted
description and identity. GPT-Live receives that compact role and speaking-style
projection plus recent canonical history. It does not receive the Agent's output
protocol, capabilities, Skills, tools, memory instructions or memory files; those
remain behind the canonical Agent session and are reached through delegation. This
keeps direct speech recognizably in character without duplicating memory authority
or adding a backend round trip to lightweight turns.

Voice identity belongs to the active Agent. The Voice Session capability adds
an optional provider-neutral Live Voice reference to Agent entities, and each
provider contributes compatible Live Voice profiles. Nova resolves that current
Agent value server-side when the call starts. The browser cannot override it.
Agents without a selection use the provider's fixed compatibility fallback;
configured but invalid references fail visibly rather than choosing another voice.

## Experience

The transition should feel like entering a call rather than opening a utility
panel:

- the active Agent portrait fills the viewport as a softly blurred, darkened
  background;
- a crisp central portrait and audio orb carry connecting, listening,
  speaking, working, muted, error, and ending states;
- live captions remain readable in a restrained glass surface;
- mute and end are the only controls, built from Leaf's shared Button primitive
  with Phosphor icons and safe-area spacing on phones;
- Escape ends the call, focus stays within the modal controls, and focus returns
  to the initiating control after the mode closes;
- surfaces, borders, text, state colours, radii, focus and composed motion come
  from the Leaf design system while the portrait treatment remains Nova-owned;
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

Nova work is a connected sub-state, not a second session. It takes
visual precedence over listening/speaking and returns to the live path when a
spoken result arrives.

## Acceptance

- No discussion, AI-session, message UID, delegation, or transcript contract
  changes.
- One provider admission per start gesture and at most one close request for the
  accepted session.
- Concurrent close requests collapse into one provider drain; replacement admission
  cannot read history until that drain and transcript flush have completed.
- AI-session injection carries signed Agent/beneficiary/discussion provenance, and
  callback retries remain idempotent by stable message UID.
- Lightweight direct turns remain low latency, while memory-, state-, fact-, tool-
  and action-dependent turns delegate to canonical Nova. Non-delegated turns are
  still durably restored after reconnect.
- The compact Live prompt includes the selected Agent's bounded identity/persona,
  but excludes backend instructions, tools and durable memory.
- The admitted provider and voice match the current Agent's validated Live Voice
  profile; browser-supplied voice/provider values are ignored by contract.
- Microphone tracks stop before the exit animation begins.
- Phone and desktop have no horizontal overflow and preserve safe-area controls.
- Opening and closing are smooth; reduced motion collapses decorative movement.
- The real product route has no page or console errors under a mocked media and
  provider boundary. A paid provider call is a separate acceptance gate.
