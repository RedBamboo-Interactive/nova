using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

public sealed class AttachMcpServerRequest
{
    /// <summary>The MCP server's entity id or slug.</summary>
    public string? Server { get; set; }

    /// <summary>Why the Agent wants it; shown on the approval card when one is needed.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// MCP servers attached to one discussion on demand, the same way as Skills: the person attaches any
/// library server at once; the discussion's own Agent attaches one on its <c>optional_mcp_servers</c>
/// (or <c>mcp_servers</c>) list at once and asks for anything else. RedLeaf's MCP gateway serves the tools.
/// </summary>
public static class DiscussionMcpEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussions/{id}/mcp-servers", async (string id, HttpContext ctx, DiscussionStore store, DiscussionMcpService mcp) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return DiscussionEndpoints.NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);
            var library = string.Equals(ctx.Request.Query["library"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            var view = await mcp.ViewAsync(discussion, library, ctx.RequestAborted);
            return Results.Ok(new
            {
                items = view.Attached,
                requests = view.Requests,
                defaults = view.Defaults,
                optional = view.Optional,
                library = view.Library,
            });
        })
            .WithSummary("Lists the MCP servers attached to a discussion on demand (items), the Agent's attach requests (requests), the Agent's default MCP servers (defaults, its mcp_servers field, on every session already) and the ones it may attach itself (optional, its optional_mcp_servers field). Query library=true adds every enabled mcp-server entity.");

        group.MapPost("/discussions/{id}/mcp-servers", async (string id, AttachMcpServerRequest request, HttpContext ctx,
                DiscussionStore store, DiscussionSkillService skills, DiscussionMcpService mcp,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            {
                var discussion = await store.GetAsync(id, ctx.RequestAborted);
                return discussion is null ? DiscussionEndpoints.NotFound() : await AttachAsync(discussion, request, ctx, skills, mcp, events);
            })
            .WithSummary("Attaches an MCP server (an mcp-server entity) to this discussion only. Body: server (entity id or slug), optional reason. A person attaches any server at once. The discussion's own Agent attaches one on its optional_mcp_servers or mcp_servers list at once (200, attached); anything else becomes a request the person approves or declines on a card (202, status approval_requested). Its tools then answer at once through the session's leaf MCP connection: leaf_mcp_tools(server) lists them and leaf_mcp_call(server, tool, arguments) calls one; a session started later lists them natively as <server>__<tool>. Another discussion never sees it.");

        group.MapDelete("/discussions/{id}/mcp-servers/{server}", async (string id, string server, HttpContext ctx,
                DiscussionStore store, DiscussionSkillService skills, DiscussionMcpService mcp,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            {
                var discussion = await store.GetAsync(id, ctx.RequestAborted);
                if (discussion is null) return DiscussionEndpoints.NotFound();
                if (DiscussionSkillEndpoints.Refusal(discussion, ctx) is { } refused) return refused;
                if (DiscussionSkillEndpoints.Caller(discussion, ctx) is not { } signed) return DiscussionSkillEndpoints.NotAllowed();
                var caller = await skills.NamePersonAsync(discussion, signed, ctx.RequestAborted);
                var result = await mcp.DetachAsync(discussion, server, caller.Text, ctx.RequestAborted);
                if (result is not var (items, removed)) return DiscussionEndpoints.NotFound();
                if (removed is not null) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ctx.RequestAborted);
                return Results.Ok(new { detached = removed is not null, items });
            })
            .WithSummary("Detaches an on-demand MCP server from this discussion (path: its entity id or slug). The person or the discussion's own Agent may. Its tools stop answering here at once; the mcp-server entity is untouched.");
    }

    internal static async Task<IResult> AttachAsync(DiscussionRead discussion, AttachMcpServerRequest request, HttpContext ctx,
        DiscussionSkillService skills, DiscussionMcpService mcp, IPluginEvents? events)
    {
        if (DiscussionSkillEndpoints.Refusal(discussion, ctx) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(request.Server))
            return Results.BadRequest(new { error = "invalid_request", message = "server (an mcp-server entity id or slug) is required" });
        if (DiscussionSkillEndpoints.Caller(discussion, ctx) is not { } signed) return DiscussionSkillEndpoints.NotAllowed();
        var ct = ctx.RequestAborted;
        var caller = await skills.NamePersonAsync(discussion, signed, ct);
        var server = await mcp.ResolveServerAsync(request.Server, ct);
        if (server is null || DiscussionMcpServers.IsDisabled(server))
            return Results.NotFound(new { error = "mcp_server_not_found", message = $"No enabled MCP server '{request.Server}'. GET /discussions/{discussion.Id}/mcp-servers?library=true lists them." });
        var now = DateTimeOffset.UtcNow;

        if (discussion.AttachedMcpServers.Any(a => a.EntityId == server.Id.ToString()))
            return Results.Ok(new { status = "attached", attached = false, items = discussion.AttachedMcpServers });

        if (caller.Kind == CapabilityAttachment.ByAgent)
        {
            var agent = await skills.GetAgentEntityAsync(discussion.AgentId, ct);
            if (agent is null || !DiscussionMcpService.AgentMayAttach(agent, server))
            {
                var pending = await mcp.RequestAsync(discussion, server, caller, request.Reason, now, ct);
                if (pending is not var (asked, added)) return DiscussionEndpoints.NotFound();
                if (added) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
                return Results.Json(new
                {
                    status = "approval_requested",
                    request = asked,
                    message = $"\"{server.Name}\" is not on your optional_mcp_servers list, so your owner decides. A card in this discussion asks; you get a note with the answer. Carry on meanwhile.",
                }, statusCode: StatusCodes.Status202Accepted);
            }
        }

        var attachment = new CapabilityAttachment(CapabilityAttachment.McpKind, server.Id.ToString(), server.Slug,
            string.IsNullOrWhiteSpace(server.Name) ? server.Slug : server.Name,
            caller.Kind, caller.Id, caller.Name, now);
        var result = await mcp.AttachAsync(discussion, attachment, caller.Text, ct);
        if (result is not var (items, wasAdded)) return DiscussionEndpoints.NotFound();
        if (wasAdded) await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
        return Results.Ok(new
        {
            status = "attached",
            attached = wasAdded,
            items,
            tools = $"leaf_mcp_tools(server: \"{server.Slug}\") lists its tools; leaf_mcp_call(server: \"{server.Slug}\", tool, arguments) calls one.",
        });
    }

    internal static async Task<IResult> DecideAsync(DiscussionRead discussion, CapabilityRequest request, string decision,
        DiscussionSkillEndpoints.CallerInfo caller, DateTimeOffset now, DiscussionMcpService mcp, DiscussionStore store,
        IPluginEvents? events, CancellationToken ct)
    {
        if (decision == "decline")
        {
            var declined = await store.DeclineCapabilityRequestAsync(discussion.EntityId, request.Id, now, ct);
            if (declined is null) return DiscussionEndpoints.NotFound();
            await mcp.NotifyDeclinedAsync(discussion, declined, caller.Text, ct);
            await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
            return Results.Ok(new { status = "declined", request = declined });
        }

        var server = await mcp.ResolveServerAsync(request.EntityId, ct);
        if (server is null || DiscussionMcpServers.IsDisabled(server))
            return Results.NotFound(new { error = "mcp_server_not_found", message = "That MCP server is no longer available." });
        var attachment = new CapabilityAttachment(CapabilityAttachment.McpKind, server.Id.ToString(), server.Slug,
            string.IsNullOrWhiteSpace(server.Name) ? server.Slug : server.Name,
            CapabilityAttachment.ByAgent, request.RequestedById, request.RequestedByName, now, caller.Id);
        var result = await mcp.AttachAsync(discussion, attachment, caller.Text, ct);
        if (result is not var (items, _)) return DiscussionEndpoints.NotFound();
        await DiscussionEndpoints.PublishDiscussionChangedAsync(events, discussion, ct);
        return Results.Ok(new { status = "attached", items });
    }
}
