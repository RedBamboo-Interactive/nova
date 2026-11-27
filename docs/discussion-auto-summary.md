# Nova discussion auto-summary

## Product decision

Nova shows a concise workstream summary beneath the discussion transcript, immediately above the composer. The discussion row shows its status and a short label. The card answers three questions.

- Where does the work stand?
- What happens next and who takes that step?
- Is a specific action waiting on me?

Nova Settings → Chat supplies a durable per-person default, initially off. Info → Overview → Behaviour supplies an optional per-discussion override with an explicit return to the Nova default. Every existing discussion starts by inheriting the default. The existing live session activity indicator and the inferred workstream state are independent.

A Provider picker workstream changed from “waiting for Storage to release the test machine” to “waiting for rollout” and finally to “picker live and verified.” All three points could have a quiet session. The browser check alone established progress toward the agreed goal. The production readback established completion.

## Ownership and lifecycle

Nova owns the preference, scheduling and summary projection. RedCompute owns ordered transcript records, the turn ledger, runtime activity and the input queue. The summary is discussion metadata. It creates no message, unread revision, provider session, user prompt, Heartbeat note or automatic action.

The existing 30-second Nova orchestration tick offers an opportunity to inspect enabled, open, owner-scoped Nova chat discussions. Heartbeat processing keeps priority. Summary work runs independently with one bounded pass at a time and repeated candidates are visited fairly. The worker uses fresh Nova service provenance scoped to the discussion, its Agent and the current verified beneficiary. It does not inherit the identity of an interactive request that triggered a reload.

A candidate becomes due three minutes after its latest canonically settled turn. A pending queue, a working/background provider, an awaiting answer with a live turn, a replaced session, or an unknown observation defers inference. Before sending context and before committing a generated result, check ownership, confidentiality, settings and the source basis. If Heartbeat or a person restarts work, the same queue and turn checks discard the old result. Failures preserve the last valid summary. Each running worker backs off and caps invalid Fast attempts at three for the same settled basis. A restart can reconsider unfinished work and a stored successful result suppresses recomputation for its turn.

A generation uses the discussion Agent's configured Fast tier and the same verified, tool-free, stateless RedCompute path as project grouping. The input is bounded, author-labelled conversation text from one authorized discussion with current machine facts. Tool payloads, attachments, hidden reasoning and unrelated discussions stay out. Conversation content is data. Inference emits plain JSON and cannot start a turn or act on its conclusions.

## Data contract

The projection contains a status code (`waiting`, `needs_you`, `in_progress`, `complete`, `unclear`), a short sidebar label, a status sentence, a next step, an optional concrete action for the person, the source, generated/updated time, and an opaque write version. The server attaches the session and turn basis for generated results and validated evidence from the bounded input. `needs_you` requires a concrete user action. A mere suggestion, ambiguous ask or model inference cannot become an approval card or trigger work. Completion requires affirmative evidence from the conversation, with `unclear` available when evidence is insufficient.

The summary is a projection with source and freshness. New work suppresses the old sidebar label and marks or hides the old card until it describes the current discussion again. Result writes use the existing discussion entity gate and compare the expected write version plus the discussion basis. A human or signed Agent correction wins over an in-flight automatic generation. Metadata-only updates publish an owner-filtered discussion invalidation. Clients refresh the projection without reloading transcript history.

## AI-native surface

RedLeaf advertises the signed Nova routes under `/discover/nova`. A person or owning Agent can read the current status and preference, follow the server's source and version, write a bounded correction, and explicitly select the discussion override. The settings API exposes the owner's durable default. Settings uses Leaf's signed browser identity because the shell hosts the panel. A person changes the owner-wide default, and the owning Agent can set a discussion override and publish or correct that discussion’s status. A correction is labelled as authored, distinct from Fast output. Writes with stale versions return a conflict with fresh state available from GET. Enabling the automation and writing an authored correction are distinct operations.

Nova injects a short capability hint into the discussion context. Agents retrieve the current projection on demand through the signed endpoint. A context delta teaches agents in already-open discussions about the feature after an upgrade. Normal progress and completion require no Agent bookkeeping. Automation is the fallback. An Agent uses the write endpoint when it has a concrete correction or wants to publish a more precise next step.

| Method and route under `/api/apps/nova` | Contract |
| --- | --- |
| `GET /discussion-summaries` | Read authorized open-discussion projections and the beneficiary's default. This is the sidebar's read model. |
| `GET /discussion-summaries/settings` | Read `{enabled}` for the signed beneficiary. |
| `PUT /discussion-summaries/settings` | Save `{enabled: true|false}` as the person. |
| `GET /discussions/{id}/summary` | Read `enabled`, `override`, `version`, `stale` and the current or most recent `summary`. |
| `PUT /discussions/{id}/summary/preference` | Send `{"enabled": true}` for on, `{"enabled": false}` for off, or `{"enabled": null}` to resume inheritance. |
| `PUT /discussions/{id}/summary` | Send `{expectedVersion, text: {status, label, overview, nextStep, userAction}}`. Use the outer `version` returned by GET, and keep `userAction` null unless the status is `needs_you`. |

A `409 summary_changed_or_disabled` response supplies the current view. Re-read it before deciding whether the correction still applies. A mutation confirms persistence and returns the updated view. The generated result includes its Fast job ID when available, the transcript epoch and high-water sequence, and the evidence message UIDs.

The same ownership rules protect the sidebar, API, context and worker. Confidential, foreign-app, LIVE and Heartbeat discussions are excluded from this first release. Ordinary access checks still apply and no raw provider result or transcript is exposed through summary endpoints or WebSocket events.

## Acceptance

- Settings default persists server-side, an explicit discussion override wins, resetting resumes inheritance, and older discussions default to off.
- A quiet turn gets one durable Fast result. Duplicate observations, provider errors, new input, restart, rollback and an Agent correction cannot publish an older result over a newer basis.
- Ordinary signed owner access works. Another owner, unrelated Agent and confidential discussions cannot read or mutate derived private state.
- Summary-only updates leave ordered transcript history, read revision, queue, callbacks and scroll anchoring intact. A new message renders through the unchanged transcript path.
- The product renders the summary immediately above the composer, recognises waiting versus needs-you by words and colour, and projects the same status into the originating sidebar row. No extra Agent reminder or Heartbeat scheduling is required.
