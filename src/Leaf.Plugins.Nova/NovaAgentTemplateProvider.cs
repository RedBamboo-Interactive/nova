using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Reads Nova's portable authored defaults from an embedded package resource.
/// Merely resolving this service is side-effect free; only RedLeaf's explicit
/// first-run setup transaction may copy the returned value into a new Agent.
/// </summary>
public sealed class NovaAgentTemplateProvider : IAgentTemplateProvider
{
    private const string ResourceName = "nova-agent-template.v1.json";

    public NovaAgentTemplateProvider()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded {ResourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var resource = JsonSerializer.Deserialize<TemplateResource>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("Nova Agent template is empty");

        Require(resource.Id, "id");
        Require(resource.TemplateVersion, "templateVersion");
        Require(resource.Name, "name");
        Require(resource.Description, "description");
        Require(resource.Identity, "identity");
        Require(resource.OutputProtocol, "outputProtocol");
        Require(resource.MemoryInstructions, "memoryInstructions");
        if (resource.DefaultSkillIds is null || resource.DefaultSkillIds.Count == 0)
            throw new InvalidDataException("Nova Agent template requires defaultSkillIds");
        if (resource.DefaultSkillIds.Any(value => !IsPackageQualifiedSkillId(value))
            || resource.DefaultSkillIds.Distinct(StringComparer.Ordinal).Count()
                != resource.DefaultSkillIds.Count)
            throw new InvalidDataException(
                "Nova Agent template defaultSkillIds must be unique plugin/skill references");
        if (resource.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported Nova Agent template schema {resource.SchemaVersion}");

        Template = new AgentTemplateDefinition(
            resource.Id!,
            resource.SchemaVersion,
            resource.TemplateVersion!,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            resource.Name!,
            resource.Description!,
            resource.Identity!,
            resource.OutputProtocol!,
            resource.MemoryInstructions!,
            resource.DefaultSkillIds);
    }

    public AgentTemplateDefinition Template { get; }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Nova Agent template field '{field}' is required");
    }

    private static bool IsPackageQualifiedSkillId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var segments = value.Split('/');
        return segments.Length == 2 && segments.All(segment => segment.Length > 0
            && segment.All(character => char.IsAsciiLetterLower(character)
                || char.IsAsciiDigit(character) || character == '-'));
    }

    private sealed class TemplateResource
    {
        public string? Id { get; init; }
        public int SchemaVersion { get; init; }
        public string? TemplateVersion { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Identity { get; init; }
        public string? OutputProtocol { get; init; }
        public string? MemoryInstructions { get; init; }
        public IReadOnlyList<string>? DefaultSkillIds { get; init; }
    }
}
