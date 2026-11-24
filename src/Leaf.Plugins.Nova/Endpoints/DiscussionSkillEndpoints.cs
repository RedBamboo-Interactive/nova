using System.Security.Claims;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

public sealed class AttachSkillRequest
{
    /// <summary>The Skill's entity id or slug.</summary>
    public string? Skill { get; set; }

    /// <summary>Why the Agent wants it; shown on the approval card when one is needed.</summary>
    public string? Reason { get; set; }
}

public sealed class SkillRequestDecision
{
    /// <summary><c>approve</c> or <c>decline</c>.</summary>
    public string? Decision { get; set; }
}

/// <summary>
/// Skills attached to one discussion on demand. The person attaches any library Skill at once. The
/// discussion's own Agent attaches a Skill on its <c>optional_skills</c> (or <c>skills</c>) list at once,
/// and asks for anything else: that request waits for the person's tap.
/// </summary>
public static class DiscussionSkillEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussions/{id}/skills", async (string id, HttpContext ctx, DiscussionStore store, DiscussionSkillService skills) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return DiscussionEndpoints.NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);
            var library = string.Equals(ctx.Request.Query["library"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var view = await skills.ViewAsync(discussion, library, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = view.Attached,
                requests = view.Requests,
                defaults = view.Defaults,
                optional = view.Optional,
                library = view.Library,
            });
        })
            .WithSummary("Lists the Skills attached to a discussion on demand (items), the Agent's attach requests (requests: pending, approved or declined), the Agent's default Skills (defaults, on every discussion already) and the Skills it may attach itself (optional, its optional_skills field). Query library=true adds every Skill in the library.");

        group.MapPost("/discussions/{id}/skills", async (string id, AttachSkillRequest request, HttpContext ctx,
                DiscussionStore store, DiscussionSkillService skills,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            {
                var discussion = await store.GetAsync(id, ctx.RequestAborted);
                return discussion is null ? DiscussionEndpoints.NotFound() : await AttachAsync(discussion, request, ctx, skills, events);
            })
            .WithSummary("Attaches a library Skill to this discussion only, beside the Agent's defaults. Body: skill (entity id or slug), optional reason. A person attaches any Skill at once. The discussion's own Agent attaches a Skill on its optional_skills or skills list at once (200, attached); anything else becomes a request the person approves or declines on a card (202, status approval_requested). On attach the Agent gets the whole SKILL.md as a note after its current turn, the package is written to <scratch>/attached-skills/<slug>/, and every later turn names it, so it survives compaction, restarts and resumes. Another discussion never sees it.");

        group.MapDelete("/discussions/{id}/skills/{skill}", async (string id, string skill, HttpContext ctx,
                DiscussionStore store, DiscussionSkillService skills,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            {
                var discussion = await store.GetAsync(id, ctx.RequestAborted);
                if (discussion is null) return DiscussionEndpoints.NotFound();
                if (Refusal(discussion, ctx) is { } refused) return refused;
                if (Caller(discussion, ctx) is not { } caller) return NotAllowed();
                var result = await skills.DetachAsync(discussion, skill, caller.Text, ctx.RequestAborted);
                if (result is not var (items, removed)) return DiscussionEndpoints.NotFound();
                if (removed is not null) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ctx.RequestAborted);
                return Results.Ok(new { detached = removed is not null, items });
            })
            .WithSummary("Detaches an on-demand Skill from this discussion (path: its entity id or slug). The person or the discussion's own Agent may. The Agent is told to stop following it; the Skill entity is untouched.");

        group.MapPost("/discussions/{id}/skill-requests/{requestId}", async (string id, string requestId, SkillRequestDecision body,
                HttpContext ctx, DiscussionStore store, DiscussionSkillService skills,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            {
                var discussion = await store.GetAsync(id, ctx.RequestAborted);
                return discussion is null ? DiscussionEndpoints.NotFound() : await DecideAsync(discussion, requestId, body, ctx, skills, store, events);
            })
            .WithSummary("The person approves or declines an Agent's request to attach a Skill. Body: decision (approve or decline). Only a person may decide; an Agent token is refused (403). Approving attaches it as for POST /skills; declining tells the Agent.");
    }

    public sealed record CallerInfo(string Kind, string? Id, string? Name, string Text);

    /// <summary>
    /// Who is attaching: a person (any signed human, at a browser or not) or the discussion's own
    /// Agent. Anyone else, another Agent or an automation, gets null and is refused.
    /// </summary>
    internal static CallerInfo? Caller(DiscussionRead discussion, HttpContext ctx)
    {
        var actor = DiscussionAccessPolicy.ReadSignedActor(ctx.User);
        if (actor is { IsAgent: true })
            return string.Equals(actor.Id, discussion.AgentId, StringComparison.OrdinalIgnoreCase)
                ? new CallerInfo(CapabilityAttachment.ByAgent, actor.Id, actor.Name, "You")
                : null;
        if (!IsPerson(ctx.User)) return null;
        var name = ctx.User.FindFirstValue("name");
        return new CallerInfo(CapabilityAttachment.ByUser, ctx.User.FindFirstValue("sub"), name,
            string.IsNullOrWhiteSpace(name) ? "Your owner" : name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
    }

    internal static bool IsPerson(ClaimsPrincipal user)
        => DiscussionAccessPolicy.IsPersonAtBrowser(user) || DiscussionAccessPolicy.IsExplicitHuman(user);

    internal static async Task<IResult> AttachAsync(DiscussionRead discussion, AttachSkillRequest request, HttpContext ctx,
        DiscussionSkillService skills, IPluginEvents? events)
    {
        if (Refusal(discussion, ctx) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(request.Skill))
            return Results.BadRequest(new { error = "invalid_request", message = "skill (an agent-skill entity id or slug) is required" });
        if (Caller(discussion, ctx) is not { } caller) return NotAllowed();
        var ct = ctx.RequestAborted;
        var skill = await skills.ResolveSkillAsync(request.Skill, ct);
        if (skill is null)
            return Results.NotFound(new { error = "skill_not_found", message = $"No Skill '{request.Skill}' in the library. GET /discussions/{discussion.Id}/skills?library=true lists them." });
        var now = DateTimeOffset.UtcNow;

        // Already here, whoever attached it: nothing to ask or send.
        if (discussion.AttachedSkills.Any(a => a.EntityId == skill.Id.ToString()))
            return Results.Ok(new
            {
                status = "attached",
                attached = false,
                skillFile = (await skills.ScratchRootAsync(discussion, ct)) is { } root
                    ? DiscussionSkills.SkillFile(root, skill.Slug).Replace('\\', '/')
                    : null,
                items = discussion.AttachedSkills,
            });

        if (caller.Kind == CapabilityAttachment.ByAgent)
        {
            var agent = await skills.GetAgentEntityAsync(discussion.AgentId, ct);
            if (agent is null || !DiscussionSkillService.AgentMayAttach(agent, skill))
            {
                var pending = await skills.RequestAsync(discussion, skill, caller, request.Reason, now, ct);
                if (pending is not var (asked, added)) return DiscussionEndpoints.NotFound();
                if (added) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
                return Results.Json(new
                {
                    status = "approval_requested",
                    request = asked,
                    message = $"\"{skill.Name}\" is not on your optional_skills list, so your owner decides. A card in this discussion asks; you get a note with the answer. Carry on meanwhile.",
                }, statusCode: StatusCodes.Status202Accepted);
            }
        }

        var attachment = new CapabilityAttachment(CapabilityAttachment.SkillKind, skill.Id.ToString(), skill.Slug,
            string.IsNullOrWhiteSpace(skill.Name) ? skill.Slug : skill.Name,
            caller.Kind, caller.Id, caller.Name, now);
        var result = await skills.AttachAsync(discussion, skill, attachment, caller.Text, ct);
        if (result is not var (items, wasAdded, skillFile)) return DiscussionEndpoints.NotFound();
        if (wasAdded) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
        return Results.Ok(new
        {
            status = "attached",
            attached = wasAdded,
            skillFile = skillFile?.Replace('\\', '/'),
            items,
        });
    }

    internal static async Task<IResult> DecideAsync(DiscussionRead discussion, string requestId, SkillRequestDecision body,
        HttpContext ctx, DiscussionSkillService skills, DiscussionStore store, IPluginEvents? events)
    {
        if (Refusal(discussion, ctx) is { } refused) return refused;
        if (!IsPerson(ctx.User))
            return Results.Json(new { error = "decision_requires_person", message = "Only a person approves or declines a Skill request." },
                statusCode: StatusCodes.Status403Forbidden);
        var decision = body.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "decline"))
            return Results.BadRequest(new { error = "invalid_request", message = "decision must be approve or decline" });
        var request = (discussion.CapabilityRequests ?? []).FirstOrDefault(r => r.Id == requestId);
        if (request is null) return Results.NotFound(new { error = "request_not_found", message = "No such Skill request on this discussion." });
        if (!request.IsPending)
            return Results.Json(new { error = "request_settled", message = $"This request was already {request.Status}.", request },
                statusCode: StatusCodes.Status409Conflict);
        var caller = Caller(discussion, ctx)!;
        var ct = ctx.RequestAborted;
        var now = DateTimeOffset.UtcNow;

        if (decision == "decline")
        {
            var declined = await store.DeclineCapabilityRequestAsync(discussion.EntityId, requestId, now, ct);
            if (declined is null) return DiscussionEndpoints.NotFound();
            await skills.NotifyDeclinedAsync(discussion, declined, caller.Text, ct);
            await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
            return Results.Ok(new { status = "declined", request = declined });
        }

        var skill = await skills.ResolveSkillAsync(request.EntityId, ct);
        if (skill is null)
            return Results.NotFound(new { error = "skill_not_found", message = "That Skill is no longer in the library." });
        var attachment = new CapabilityAttachment(CapabilityAttachment.SkillKind, skill.Id.ToString(), skill.Slug,
            string.IsNullOrWhiteSpace(skill.Name) ? skill.Slug : skill.Name,
            CapabilityAttachment.ByAgent, request.RequestedById, request.RequestedByName, now, caller.Id);
        var result = await skills.AttachAsync(discussion, skill, attachment, caller.Text, ct);
        if (result is not var (items, _, skillFile)) return DiscussionEndpoints.NotFound();
        await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
        return Results.Ok(new { status = "attached", skillFile = skillFile?.Replace('\\', '/'), items });
    }

    private static IResult? Refusal(DiscussionRead discussion, HttpContext ctx)
    {
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);
        if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
        if (DiscussionMutationPolicy.RejectSend(discussion)) return DiscussionEndpoints.Closed();
        return null;
    }

    private static IResult NotAllowed()
        => Results.Json(new
        {
            error = "attach_not_allowed",
            message = "Only the person or this discussion's own Agent changes its Skills.",
        }, statusCode: StatusCodes.Status403Forbidden);
}
