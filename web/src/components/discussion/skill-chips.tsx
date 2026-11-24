import { useCallback, useEffect, useMemo, useState } from "react"
import { PermissionApprovalCard } from "@redbamboo/chat"
import { EntityPickerDialog, type EntityPickerItem } from "@redbamboo/ui"
import { api } from "../../lib/api"
import {
  filterSkills,
  pendingSkillRequests,
  skillAvailability,
  skillAvailabilityLabel,
  skillChipTitle,
  skillOriginLabel,
  skillRequestApproval,
  skillRequestPath,
  skillsPath,
  type DiscussionSkillsResponse,
  type SkillAttachment,
  type SkillRequest,
} from "../../lib/discussion-skills"

const CHIP = "inline-flex max-w-full items-center gap-1.5 rounded-md bg-overlay-6 px-2 py-1 text-[11px] font-medium text-text-secondary"

/**
 * Skills attached to this discussion on demand, one chip each, with who put it here, and the
 * Agent's requests waiting for the person's tap. Same row anatomy as `WorktreeChips`. Hidden when
 * there is nothing to show; the picker opens from the conversation menu (Attach Skill).
 */
export function SkillChips({ discussionId, skills, requests, agentName, disabled }: {
  discussionId: string
  skills: readonly SkillAttachment[] | undefined
  requests: readonly SkillRequest[] | undefined
  agentName: string | null | undefined
  disabled?: boolean
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const pending = pendingSkillRequests(requests)
  if (!skills?.length && !pending.length && !error) return null

  const detach = async (skill: SkillAttachment) => {
    setBusy(skill.entityId)
    setError(null)
    try {
      await api.delete(`${skillsPath(discussionId)}/${encodeURIComponent(skill.entityId)}`)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "The Skill could not be detached.")
    } finally {
      setBusy(null)
    }
  }

  const decide = async (request: SkillRequest, decision: string) => {
    await api.post(skillRequestPath(discussionId, request.id), { decision: decision === "deny" ? "decline" : "approve" })
  }

  return (
    <div data-slot="discussion-skills" className="flex shrink-0 flex-col gap-1.5 border-b border-overlay-6 px-4 py-1.5">
      {!!skills?.length && (
        <div role="list" aria-label="Skills attached to this discussion" className="flex flex-wrap items-center gap-1.5">
          {skills.map(skill => (
            <span key={skill.entityId} role="listitem" data-slot="discussion-skill-chip" data-origin={skill.attachedBy}
              title={skillChipTitle(skill, agentName)} className={CHIP}>
              <i aria-hidden="true" className="ph-bold ph-puzzle-piece shrink-0 text-[12px]" />
              <span className="min-w-0 truncate">{skill.name}</span>
              <span className="shrink-0 font-normal text-text-muted">· {skillOriginLabel(skill, agentName)}</span>
              {!disabled && (
                <button
                  type="button"
                  data-slot="discussion-skill-detach"
                  aria-label={`Detach ${skill.name}`}
                  title="Detach from this discussion"
                  disabled={busy === skill.entityId}
                  onClick={() => void detach(skill)}
                  className="-mr-0.5 inline-flex shrink-0 items-center rounded-sm text-text-muted transition-colors hover:text-contrast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring-a50 disabled:opacity-50"
                >
                  <i aria-hidden="true" className={busy === skill.entityId ? "ph-bold ph-circle-notch animate-spin text-[11px]" : "ph-bold ph-x text-[11px]"} />
                </button>
              )}
            </span>
          ))}
        </div>
      )}
      {pending.map(request => (
        <PermissionApprovalCard
          key={request.id}
          className="my-0"
          approval={skillRequestApproval(request)}
          onDecide={disabled ? undefined : decision => decide(request, decision)}
        />
      ))}
      {error && <p role="alert" className="text-xs text-red-400">{error}</p>}
    </div>
  )
}

/**
 * The Skill library as the design system's entity picker, read through this discussion's Skills
 * route so each row says where it stands here (attached, always on, attachable by the Agent).
 */
export function AttachSkillDialog({ discussionId, agentName, open, onOpenChange, onError }: {
  discussionId: string
  agentName: string | null | undefined
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The dialog has closed by then, so the host says it (a toast). */
  onError: (message: string) => void
}) {
  const [view, setView] = useState<DiscussionSkillsResponse | null>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    const next = await api.get<DiscussionSkillsResponse>(`${skillsPath(discussionId)}?library=true`, signal)
    setView(next)
    return next
  }, [discussionId])

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    load(controller.signal).catch(() => {})
    return () => controller.abort()
  }, [open, load])

  const toItem = useCallback((summary: DiscussionSkillsResponse["defaults"][number], current: DiscussionSkillsResponse): EntityPickerItem => ({
    entity: { id: summary.entityId, typeSlug: "agent-skill", name: summary.name },
    slug: summary.slug,
    visual: { icon: "ph-bold ph-puzzle-piece" },
    subtitle: skillAvailabilityLabel(skillAvailability(summary, current), agentName) ?? (summary.description || undefined),
  }), [agentName])

  const source = useMemo(() => ({
    search: async ({ query }: { query: string }, signal: AbortSignal) => {
      const current = view ?? await load(signal)
      return filterSkills(current.library ?? [], query).map(summary => toItem(summary, current))
    },
    resolve: async ({ id }: { id: string }, signal: AbortSignal) => {
      const current = view ?? await load(signal)
      const found = (current.library ?? []).find(summary => summary.entityId === id || summary.slug === id)
      return found ? toItem(found, current) : null
    },
  }), [view, load, toItem])

  const attach = async (id: string) => {
    try {
      await api.post(skillsPath(discussionId), { skill: id })
    } catch (failure) {
      onError(failure instanceof Error ? failure.message : "The Skill could not be attached.")
    }
  }

  return (
    <EntityPickerDialog
      open={open}
      onOpenChange={onOpenChange}
      targetType="agent-skill"
      typeLabel="Skill"
      title="Attach a Skill"
      description={`Only this discussion gets it, beside ${agentName?.trim() || "the agent"}'s own Skills.`}
      searchPlaceholder="Search Skills"
      source={source}
      onSelect={id => void attach(id)}
    />
  )
}
