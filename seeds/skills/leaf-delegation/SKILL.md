---
name: leaf-delegation
description: "Delegate bounded implementation or research to persistent Leaf Code sessions, verify the receiving workspace has the required Leaf Skills and knowledge, review results as Nova, and continue the same session. Use when handing work to Code or inspecting, reviewing, or continuing a delegation."
---

# Leaf delegation

Delegate work through Nova's product API to a persistent RedCompute session. This is a Leaf
capability, not the harness feature for spawning internal sub-agents. A rule that only restricts
internal sub-agents does not disable this API. Delegation is still a mutation: preserve the user's
scope and obtain any separate authority required for rebuilds, publication, destructive changes,
remote creation, or external communication.

## Nova owns the work

Keep product judgment, architecture, established decisions, and acceptance review with Nova. Delegate a bounded implementation or research task when the receiving session has the context needed to do it well. A separate session does not inherit this discussion, Nova's memory, identity instructions, or selected Skills merely because the response names Nova as its Agent.

For repository-backed delegation, the working directory is the repository checkout. The current delegate endpoint does not forward Agent instructions or selected Skill packages to that session. Supplying `agent` resolves attribution and workspace/provider defaults; it does not make the Agent workspace's Skills discoverable from a different repository. Do not invent a `skills`, `addDirs`, or `developerInstructions` request field: those are not in the delegate request contract.

## Prepare the receiving session's Leaf knowledge

Before delegating Leaf work, inspect the canonical checkout's applicable instructions and available Skill packages. Resolve the selected Agent and its Skills through their authoritative entities or generated workspace projection. Verify actual availability; a Skill name in this prompt or the existence of a Skill entity does not establish that the worker can discover or read it.

Select only knowledge relevant to the task:

- Leaf architecture, API discovery, entities, signed identity, plugins, scratch and rebuild conventions: `red-suite`, or the installation's `leaf-foundations` and `leaf-engineering` packages when those are available. Read the current packages before passing them on.
- Chat, transcripts, streaming, conversation state or restart recovery: the installed chat-stability Skill, plus the relevant authoritative journal entry, protected invariant and regression risks. Resolve its referenced memory files from the owning Agent workspace; do not assume they exist in the source repository.
- Leaf UI implementation and acceptance: the installed `playwright-testing` Skill, with the real shell route and required user-visible behavior.
- RedCompute provider work: the installed `build-redcompute-provider` Skill and the current provider/session contracts.
- Other optional extensions: their owning Skill and installed contract when the task calls for them, rather than Nova's entire personal Skill set.

Include a short knowledge section in the delegated prompt. Give each required Skill's exact readable entrypoint and the relevant reference or journal paths, resolved for this installation. Explicitly tell the worker to read them before changing source. If a package cannot be read from the receiving environment, include its current authoritative instructions and necessary references in the prompt, within the task's authorized data scope. Never include raw credentials. If neither method provides the required knowledge, do the work directly or report the missing context; do not send an under-informed worker anyway.

Ask the worker to establish the checkout, loaded Skills/references, relevant decisions and integration ownership before edits. Review that evidence in its session output. Missing required context is a reason to correct the handoff before implementation, not something to discover at completion. On continuation, recheck context when scope changes or a required contract has changed.

Never repair discovery by editing generated `AGENTS.md`, `CLAUDE.md`, `.agents/skills`, or `.claude/skills` files, or by copying private Nova memory into a repository. Agent/Skill entities own those projections. Automatic attachment of Skills to repository sessions requires a separately scoped product change; a prompt handoff is not proof that attachment exists.

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

Give the delegated session the concrete outcome, repository scope, verified required Skills and references, decisions already made,
acceptance behavior, required verification, dirty-worktree boundaries, and anything that must remain
untouched. Keep work that may edit the same files in one sequential session; use parallel sessions
only for independent scopes.
