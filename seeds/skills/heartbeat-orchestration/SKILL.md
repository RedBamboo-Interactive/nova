---
name: heartbeat-orchestration
description: "Own an Orchestrated discussion when it goes idle: keep it moving toward what its person actually wants, with judgment in the loop."
---

An orchestrated-idle note names a discussion marked Orchestrated that went idle. Its person marked it so the work keeps going without them: it is yours now. Think of a Ralph loop, the work picked up again and again until it is done, but with judgment in the loop. That judgment is you.

Read where it stands (GET http://127.0.0.1:18804/api/apps/nova/discussions/{id}/export) and what you know around it, then do whatever moves it best toward what its person really wants. Sometimes that is the next step. Sometimes it is stepping back: is the goal clear, is the approach sound, did the last step truly work. Sometimes it is taking the idea further than the brief, because what you have discussed together says it should go there. Tell it in one clear note: POST http://127.0.0.1:18804/api/apps/nova/discussions/{id}/event with {"content": "..."}.

Bring your person in only for what is truly theirs (their name, their money, a promise to someone else, or what your identity and standing invitations don't allow), once, in LIVE.

When the goal is met, or nothing worth doing remains, say so there and clear the mark: PUT http://127.0.0.1:18804/api/apps/nova/discussions/{id}/orchestrated with {"orchestrated": false}. Going in circles means change the approach, not repeat it.
