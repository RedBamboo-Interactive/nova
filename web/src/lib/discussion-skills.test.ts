import test from "node:test"
import assert from "node:assert/strict"
import {
  filterSkills,
  pendingSkillRequests,
  readSkillAttachments,
  skillAvailability,
  skillAvailabilityLabel,
  skillChipTitle,
  skillOriginLabel,
  skillRequestApproval,
  skillRequestPath,
  skillsPath,
  capabilityPath,
  type SkillAttachment,
  type SkillRequest,
} from "./discussion-skills.ts"

const mine: SkillAttachment = { kind: "skill", entityId: "e1", slug: "mail", name: "Mail", attachedBy: "user", attachedAt: "2026-11-24T19:00:00Z" }
const hers: SkillAttachment = { ...mine, entityId: "e2", slug: "outfit", name: "Outfit", attachedBy: "agent" }
const request: SkillRequest = {
  id: "r1", kind: "skill", entityId: "e3", slug: "voice", name: "Voice", reason: "Laurent asked for a voice note",
  requestedById: "nova", requestedAt: "2026-11-24T19:00:00Z", status: "pending",
}

test("a chip says who put the Skill here", () => {
  assert.equal(skillOriginLabel(mine, "Nova"), "you")
  assert.equal(skillOriginLabel(hers, "Nova"), "Nova")
  assert.equal(skillOriginLabel(hers, null), "agent")
  assert.match(skillChipTitle({ ...hers, approvedById: "me" }, "Nova"), /Nova asked, you approved\. This discussion only\./)
})

test("a waiting request becomes the shared approval card with attach-or-not only", () => {
  const approval = skillRequestApproval(request)
  assert.equal(approval.requestId, "r1")
  assert.equal(approval.outcome, null)
  assert.equal(approval.request.toolName, "Attach Skill · Voice")
  assert.equal(approval.request.summary, "Laurent asked for a voice note")
  assert.deepEqual(approval.request.options, ["allow_once", "deny"])
})

test("only pending requests draw a card", () => {
  assert.deepEqual(pendingSkillRequests([request, { ...request, id: "r2", status: "declined" }]).map(r => r.id), ["r1"])
  assert.deepEqual(pendingSkillRequests(undefined), [])
})

test("the picker says where each Skill stands for this discussion", () => {
  const view = { items: [mine], defaults: [{ entityId: "d", slug: "d", name: "D", description: "" }], optional: [{ entityId: "o", slug: "o", name: "O", description: "" }] }
  assert.equal(skillAvailability({ entityId: "e1" }, view), "attached")
  assert.equal(skillAvailability({ entityId: "d" }, view), "default")
  assert.equal(skillAvailability({ entityId: "o" }, view), "optional")
  assert.equal(skillAvailability({ entityId: "x" }, view), null)
  assert.equal(skillAvailabilityLabel("optional", "Nova"), "Nova may attach it")
  assert.equal(skillAvailabilityLabel(null, "Nova"), undefined)
})

test("search matches name, slug and description", () => {
  const skills = [
    { entityId: "1", slug: "smart-home", name: "Smart home", description: "Sonos and Hue" },
    { entityId: "2", slug: "mail", name: "Mail", description: "Gmail, read-only" },
  ]
  assert.deepEqual(filterSkills(skills, "hue").map(s => s.slug), ["smart-home"])
  assert.deepEqual(filterSkills(skills, "MAIL").map(s => s.slug), ["mail"])
  assert.equal(filterSkills(skills, "  ").length, 2)
})

test("records from the server are read defensively", () => {
  assert.deepEqual(readSkillAttachments([mine, null, { slug: "x" }]).map(s => s.slug), ["mail"])
  assert.deepEqual(readSkillAttachments("nope"), [])
})

test("routes escape the ids", () => {
  assert.equal(skillsPath("d 1"), "/api/apps/nova/discussions/d%201/skills")
  assert.equal(skillRequestPath("d1", "r/1"), "/api/apps/nova/discussions/d1/capability-requests/r%2F1")
  assert.equal(capabilityPath("d 1", "mcp"), "/api/apps/nova/discussions/d%201/mcp-servers")
  assert.equal(capabilityPath("d1", "skill"), "/api/apps/nova/discussions/d1/skills")
})

test("an MCP server request and chip say MCP server", () => {
  const approval = skillRequestApproval({ ...request, kind: "mcp", name: "Galaxus" })
  assert.equal(approval.request.toolName, "Attach MCP server · Galaxus")
  assert.equal(approval.key, "mcp-request:r1")
  assert.match(skillChipTitle({ ...hers, kind: "mcp" }, "Nova"), /Leaf's MCP gateway/)
})
