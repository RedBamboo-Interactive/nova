---
name: leaf-delegation
description: "Delegate implementation or research from Nova to persistent Leaf Code sessions, continue or review delegated work, and distinguish Leaf delegation from internal harness sub-agents. Use when the user asks Nova to delegate work, inspect a delegation, or continue an existing Code session."
---

# Leaf delegation

Delegate work through Nova's product API to a persistent RedCompute session. This is a Leaf
capability, not the harness feature for spawning internal sub-agents. A rule that only restricts
internal sub-agents does not disable this API. Delegation is still a mutation: preserve the user's
scope and obtain any separate authority required for rebuilds, publication, destructive changes,
remote creation, or external communication.

## Route and identity

Use the installed Nova endpoint:

```text
POST http://127.0.0.1:18804/api/apps/nova/delegate
```

Require `REDLEAF_EXECUTION_TOKEN` and send it as a bearer only to trusted loopback RedLeaf or
RedCompute. Never retry without the token or forward it to another origin. Verify
`/auth/execution-context` when the accepted actor or beneficiary is in doubt.

Plugin-owned routes are not always listed by RedLeaf discovery. This packaged Skill is the route
contract; also verify that Nova and RedCompute are healthy. Code supplies the session UI, but
`navigate` is best effort and UI availability is separate from prompt delivery.

## Start repository-backed work

Resolve the intended checkout through active Repository entities. When Code is installed, its
canonical projection is:

```text
GET http://127.0.0.1:18804/api/apps/codered/repositories
```

Match the repository entity by identity or remote, not by a remembered machine path. Then send:

```json
{
  "repository": "<repository entity id or slug>",
  "prompt": "<bounded outcome and acceptance criteria>",
  "discussionId": "<current discussion id>",
  "qualityTier": "deep",
  "navigate": false
}
```

For Agent-initiated and background delegation, omit `navigate` or keep it `false`. Successful
delegation records a magenta marker in the requesting discussion; the user can open its detail and
navigate to the Code session from the client they are using. Never set `navigate: true` from an Agent
execution: delegation must not steal the active route on any connected Leaf client.

For non-repository work, `agent` may select the Agent workspace. `projectPath` is compatibility-only
and must exactly match an active Repository checkout. Creating a local repository, creating a remote
repository, and delegating work are separate operations.

Prefer the installation's abstract quality tier. Set `model` or `provider` only when the user names
one or verified provider-specific behavior is required. Do not send `dockerImage`; it is not part of
the delegate request.

## Continue existing work

Continue the same session so its transcript and context remain intact:

```json
{
  "sessionId": "<existing session id>",
  "prompt": "<review feedback or next bounded task>",
  "discussionId": "<current discussion id>"
}
```

Continuation resumes stopped or errored sessions when possible, sends one idempotent prompt, and
registers a fresh terminal callback.

## Verify delivery and completion

Treat the response fields independently. Delegation succeeded only when `promptSent` is `true`.
`callbackRegistered` proves notification wiring, not implementation quality. `delegationEventRecorded`
confirms that the requesting discussion received its semantic marker. A newly created session
whose prompt cannot be accepted is cleaned up by Nova.

With `discussionId`, terminal state produces a Nova session-complete event in that discussion.
Afterward, inspect the repository status and artifacts, read the exact RedCompute session when
needed, and verify tests plus real product behavior. Continue the same session with concrete feedback
when revisions are needed.

Persistent Claude and Codex sessions can emit correlated questions. Treat pending questions as
control state and answer them through the session question route. One-shot inference closes input
after its prompt and cannot ask interactively.

## Prompt contract

Give the delegated session the concrete outcome, repository scope, decisions already made,
acceptance behavior, required verification, dirty-worktree boundaries, and anything that must remain
untouched. Keep work that may edit the same files in one sequential session; use parallel sessions
only for independent scopes.
