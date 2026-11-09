import type { AccessLevel } from "@redbamboo/chat"

const LEVELS: readonly string[] = ["plan", "ask", "auto", "full"]

/** RedLeaf Settings section where Full access is turned on (settings-panel.tsx). */
export const AGENT_PERMISSIONS_SETTINGS_SECTION = "agent-permissions"

/**
 * RedLeaf has no URL for a Settings section: the shell opens one on an
 * `open-settings` window event. The Info modal only takes an href, so the link
 * carries this inert fragment and {@link openSettingsFromLink} turns the click
 * into that event.
 */
export const FULL_ACCESS_SETTINGS_HREF = `#settings=${AGENT_PERMISSIONS_SETTINGS_SECTION}`

/** The shared Info modal's "Enable Full access in Settings" link. */
const FULL_ACCESS_LINK_SELECTOR = 'a[data-slot="full-access-settings-link"]'

interface LinkClick {
  target: EventTarget | null
  preventDefault(): void
}

interface SettingsWindow {
  dispatchEvent(event: Event): boolean
}

/**
 * A click on the Full access settings link opens RedLeaf Settings on the Agent
 * permissions section instead of following the fragment. Returns whether it did.
 */
export function openSettingsFromLink(event: LinkClick, target: SettingsWindow = window): boolean {
  const element = event.target as { closest?: (selector: string) => unknown } | null
  if (!element || typeof element.closest !== "function" || !element.closest(FULL_ACCESS_LINK_SELECTOR)) return false
  event.preventDefault()
  target.dispatchEvent(new CustomEvent("open-settings", { detail: { section: AGENT_PERMISSIONS_SETTINGS_SECTION } }))
  return true
}

export function readAccessLevel(value: unknown): AccessLevel | null {
  return typeof value === "string" && LEVELS.includes(value) ? value as AccessLevel : null
}

/** A provider's supported levels; null when the session reports none (the Info modal then hides the row). */
export function readAccessLevels(value: unknown): AccessLevel[] | null {
  if (!Array.isArray(value)) return null
  const levels = value.map(readAccessLevel).filter((level): level is AccessLevel => level !== null)
  return levels.length ? levels : null
}

/**
 * `fullAccess.enabled` from RedLeaf's policy (already effective: false when it
 * was accepted on an older warning). Undefined when the policy could not be
 * read, which leaves the Full option to RedCompute to refuse.
 */
export function readFullAccessEnabled(policy: unknown): boolean | undefined {
  const fullAccess = policy && typeof policy === "object"
    ? (policy as { fullAccess?: unknown }).fullAccess
    : undefined
  const enabled = fullAccess && typeof fullAccess === "object"
    ? (fullAccess as { enabled?: unknown }).enabled
    : undefined
  return typeof enabled === "boolean" ? enabled : undefined
}

interface ConfigOption {
  value: string
  aliases?: string[]
}

/**
 * The Info modal turns every option row into a selector once it has
 * `onConfigChange`. Nova changes only the access level there, so the Quality
 * row gets the session's own option alone: it keeps its label and colour and
 * offers nothing Nova could not do. Undefined when no option matches.
 */
export function currentOptionOnly<T extends ConfigOption>(options: readonly T[], value: string | null | undefined): T[] | undefined {
  if (!value) return undefined
  const current = options.filter(option => option.value === value || option.aliases?.includes(value))
  return current.length ? current.slice(0, 1) : undefined
}

type Post = <T>(path: string, body: unknown) => Promise<T>

/**
 * Change a session's access level. A refusal rejects with the server's own
 * message (raise_requires_person, full_access_not_enabled, above_session_cap),
 * which the Info modal shows under the Permissions row. A level the provider
 * could not apply (`ok: false`) is a refusal too. Resolves to the level now in force.
 */
export async function changeAccessLevel(post: Post, sessionId: string, level: AccessLevel): Promise<AccessLevel> {
  const result = await post<{ accessLevel?: unknown; ok?: unknown }>(
    `/ai-session/sessions/${encodeURIComponent(sessionId)}/access-level`, { level })
  const applied = readAccessLevel(result?.accessLevel)
  if (result?.ok !== true || applied !== level)
    throw new Error(`The session couldn't switch to ${label(level)}${applied ? `; it stays at ${label(applied)}` : ""}.`)
  return applied
}

function label(level: AccessLevel): string {
  return level.charAt(0).toUpperCase() + level.slice(1)
}
