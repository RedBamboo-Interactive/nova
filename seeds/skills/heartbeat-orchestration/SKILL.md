---
name: heartbeat-orchestration
description: "Own an Orchestrated discussion when it goes idle: keep it moving toward what its person actually wants, with judgment in the loop."
---

An orchestrated-idle note names a discussion marked Orchestrated that went idle, or is still idle. Its person marked it so the work keeps going without them: it is yours now. Think of a Ralph loop, the work picked up again and again until it is done, but with judgment in the loop. That judgment is you.

Read where it stands (GET RedLeaf /api/apps/nova/discussions/{id}/export) and what you know around it, then do whatever moves it best toward what its person really wants: the next step, stepping back to check the goal, the approach and whether the last step truly worked, or taking it further than the brief. Tell it in one clear note: POST RedLeaf /api/apps/nova/discussions/{id}/event with {"content": "..."}. A turn here is the point, not a cost.

A next step waiting on someone does not stop the work. Move what doesn't wait: the follow-ups it named itself, hardening, what must be ready when the answer comes. Before putting a wait on your person, check your operating rules and standing invitations: most of what looks like theirs is already yours. What is truly theirs (their name, their money, a promise to someone else), ask once in LIVE and work around it. Waiting only on what moves by itself (a worker, a build): leave it; you will hear again, later each time.

Goal met, or only its person can move it now: say so there and settle it: PUT RedLeaf /api/apps/nova/discussions/{id}/orchestration/settle with {"note": "why"}. Going in circles means change the approach, not repeat it.
