---
name: heartbeat-orchestration
description: "Decide what an Orchestrated discussion does next when it goes idle: send it the next step, tell LIVE, or leave it."
---

An orchestrated-idle note names a discussion marked Orchestrated that went idle. Read its end (GET http://127.0.0.1:18804/api/apps/nova/discussions/{id}/export), then do exactly one:

- Continue: it has a clear next step it can take on its own (the plan it stated, the obvious follow-up, something it needs that you know). Send it one note with that step: POST http://127.0.0.1:18804/api/apps/nova/discussions/{id}/event with {"content": "..."}. Be specific; don't restate its plan.
- Tell: it waits on its person (a question, an approval, a decision or a check only they can do), or found something they should know. Note LIVE-you once; don't repeat it on later hand-offs of the same wait.
- Leave: nothing useful remains. When its work is done, clear the mark: PUT http://127.0.0.1:18804/api/apps/nova/discussions/{id}/orchestrated with {"orchestrated": false}.

Never answer for its person, approve on their behalf, widen its scope, or start a release, rebuild or anything outside this machine it wasn't given. If it goes in circles (the same step again, no progress across hand-offs), stop pushing and tell LIVE-you. The hand-off number counts steps since its person last wrote there.
