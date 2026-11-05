using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>One Protocol the Agent selected, as it is rendered after its Output Protocol.</summary>
public sealed record AgentProtocolBlock(string Name, string Instructions);

/// <summary>
/// The Agent's <c>protocols</c> field: short kernel-owned <c>agent-protocol</c> conduct blocks.
/// RedLeaf renders them into the generated workspace instructions; this renders the same blocks for
/// the physical and disposable fallback workspaces Nova still compiles itself. One over the cap,
/// empty, missing or deleted is skipped.
/// </summary>
public static class AgentProtocols
{
    public const string Field = "protocols";
    public const string EntityType = "agent-protocol";
    public const int MaxInstructionLength = 1500;

    public static IReadOnlyList<string> References(JsonObject data)
        => data[Field] is JsonArray list
            ? list.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text.Trim() : null)
                .Where(text => !string.IsNullOrEmpty(text))
                .Select(text => text!)
                .ToList()
            : [];

    public static async Task<IReadOnlyList<AgentProtocolBlock>> ResolveAsync(
        IEntityStore store, JsonObject agentData, CancellationToken ct)
    {
        var protocols = new List<LeafEntity>();
        foreach (var reference in References(agentData))
        {
            try
            {
                var entity = Guid.TryParse(reference, out var id)
                    ? await store.GetAsync(id, ct)
                    : await store.GetBySlugAsync(EntityType, reference, ct);
                if (entity is not null) protocols.Add(entity);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return Select(protocols);
    }

    /// <summary>The usable Protocols, in the given order, each at most once.</summary>
    public static IReadOnlyList<AgentProtocolBlock> Select(IEnumerable<LeafEntity> protocols)
    {
        var seen = new HashSet<Guid>();
        var result = new List<AgentProtocolBlock>();
        foreach (var protocol in protocols)
        {
            if (protocol.TypeSlug != EntityType || !seen.Add(protocol.Id)) continue;
            var instructions = protocol.Data["instructions"] is JsonValue value
                && value.TryGetValue<string>(out var text) ? text.Trim() : "";
            if (instructions.Length == 0 || instructions.Length > MaxInstructionLength) continue;
            result.Add(new AgentProtocolBlock(protocol.Name, instructions));
        }
        return result;
    }

    /// <summary>The Agent's own Output Protocol, then each selected Protocol as a titled block.</summary>
    public static string Render(string? outputProtocol, IReadOnlyList<AgentProtocolBlock>? protocols)
    {
        var text = new StringBuilder((outputProtocol ?? "").Trim());
        foreach (var protocol in protocols ?? [])
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append("## ").Append(protocol.Name.Trim()).Append('\n').Append(protocol.Instructions);
        }
        return text.ToString();
    }
}
