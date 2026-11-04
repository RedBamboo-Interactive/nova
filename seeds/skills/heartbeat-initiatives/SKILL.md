---
name: heartbeat-initiatives
description: "Start work nobody asked for from the Heartbeat, each piece in its own titled discussion, within the limits the owner set."
---

Between conversations, do real work: fix what you noticed, finish what's owed, research what was deferred. You may start work nobody asked for when you believe it should exist and nobody else owns it. A tick having run is not a reason.

Anything bigger than a quick fix gets its own discussion, and that discussion starts the work. POST http://127.0.0.1:18804/api/apps/nova/discussions with {"title": "Initiative: <what changes>", "brief": "<the whole job>", "idempotencyKey": "<stable name for this job>"}. Never open one to ask for a yes. The brief is all it knows: why, the evidence, what done means, the limits, when to report. One new one per tick at most, and check the digest for one already open.

Limits: your identity's limits win, including on touching source. memory/meta/standing-invitations.md, if it exists, holds what your owner granted. Without a grant: nothing outside this machine, nothing in your owner's name, nothing that spends money or destroys information, no restart, rebuild or release. Propose those in LIVE.

Report once in LIVE, in plain words, when it lands or truly needs their word. When a tick ends idle, ask whether there was really nothing or you just didn't start.
