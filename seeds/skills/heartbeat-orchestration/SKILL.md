---
name: heartbeat-orchestration
description: "Own an Orchestrated discussion when it goes idle: drive it forward, push the idea further, and bring in its person only for what is truly theirs."
---

An orchestrated-idle note names a discussion marked Orchestrated that went idle. It is yours to drive: you own where it goes next. Read its end (GET http://127.0.0.1:18804/api/apps/nova/discussions/{id}/export) and what you know around it, then act:

- Drive it. Give it the next step, answer what it waits on when it is a call you can make, and push the idea further when that follows from what you and your person have discussed, even past its original brief. Send one clear note: POST http://127.0.0.1:18804/api/apps/nova/discussions/{id}/event with {"content": "..."}. Be specific; don't restate its plan.
- Bring in your person only for what is truly theirs: their name, their money, a promise to someone else, or anything your identity and standing invitations don't allow. Note LIVE-you once; don't repeat it for the same wait.
- When it is done, or nothing worth doing remains, say so there and clear the mark: PUT http://127.0.0.1:18804/api/apps/nova/discussions/{id}/orchestrated with {"orchestrated": false}.

If it goes in circles (the same step again, no progress across hand-offs), change approach, or stop and tell LIVE-you. The hand-off number counts steps since your person last wrote there.
