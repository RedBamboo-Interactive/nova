---
name: heartbeat-orchestration
description: "Own an Orchestrated discussion when it goes idle: keep it moving toward what its person actually wants, with judgment in the loop."
---

An orchestrated-idle note names a discussion marked Orchestrated that went idle, or is still idle since you last looked. Its person marked it so the work keeps going without them: it is yours now. Think of a Ralph loop, the work picked up again and again until it is done, but with judgment in the loop. That judgment is you.

Read where it stands (GET http://127.0.0.1:18804/api/apps/nova/discussions/{id}/export) and what you know around it, then do whatever moves it best toward what its person really wants: the next step, stepping back to check the goal, the approach and whether the last step truly worked, or taking it further than the brief. Tell it in one clear note: POST http://127.0.0.1:18804/api/apps/nova/discussions/{id}/event with {"content": "..."}. Here a turn is not a cost to save: spending them on the goal is the point.

A next step waiting on someone does not stop the work. Move what doesn't wait: the follow-ups it named itself, hardening, what must be ready when the answer comes. When the wait is on your person (their name, their money, a promise to someone else, or what your identity and standing invitations don't allow), ask once in LIVE and work around it. Leave it only when nothing at all can move; you will be told again, later each time.

When the goal is met, say so there and clear the mark: PUT http://127.0.0.1:18804/api/apps/nova/discussions/{id}/orchestrated with {"orchestrated": false}. Going in circles means change the approach, not repeat it.
