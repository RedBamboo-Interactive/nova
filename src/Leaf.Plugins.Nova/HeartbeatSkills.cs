using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;

namespace Leaf.Plugins.Nova;

/// <summary>A Skill small enough to travel with every tick.</summary>
public sealed record HeartbeatSkill(string Name, string Instructions);

/// <summary>A listed Skill the Heartbeat will not carry, and why, in words its owner can act on.</summary>
public sealed record RefusedHeartbeatSkill(string Name, string Reason);

public sealed record HeartbeatSkillSet(IReadOnlyList<HeartbeatSkill> Loaded, IReadOnlyList<RefusedHeartbeatSkill> Refused)
{
    public static readonly HeartbeatSkillSet Empty = new([], []);
}

/// <summary>
/// What an Agent's Heartbeat does beyond being present: an owner-chosen list of ordinary Skills on
/// the Agent, written into every tick rather than opened on demand, because a rule the Agent may
/// never open is not a rule. Every tick carries the whole list, so taking a Skill off takes effect
/// on the next tick. That is only affordable because each one is small; a Skill that needs more
/// points at a full Skill from the Agent's Skills list.
/// </summary>
public static class HeartbeatSkills
{
    public const string Field = "heartbeat_skills";
    public const string SkillType = "agent-skill";
    public const int MaxInstructionLength = 1500;

    /// <summary>Attached once to a LIVE Agent with no list. They are what the Heartbeat always did.</summary>
    public static readonly IReadOnlyList<string> DefaultSlugs =
    [
        "heartbeat-initiatives",
        "heartbeat-conversation-nudges",
        "heartbeat-action-items",
        "heartbeat-leaf-discord",
    ];

    public static HeartbeatSkillSet Select(IEnumerable<LeafEntity> skills)
    {
        var loaded = new List<HeartbeatSkill>();
        var refused = new List<RefusedHeartbeatSkill>();
        foreach (var skill in skills)
        {
            var instructions = Text(skill.Data, "instructions").Trim();
            if (instructions.Length == 0)
                refused.Add(new(skill.Name, "it has no instructions"));
            else if (instructions.Length > MaxInstructionLength)
                refused.Add(new(skill.Name, $"it is {instructions.Length} characters and the limit is {MaxInstructionLength}. Shorten it and point at a full Skill for the rest"));
            else
                loaded.Add(new(skill.Name, instructions));
        }
        return new(loaded, refused);
    }

    /// <summary>The list as the Agent reads it. It replaces every earlier one, which is what makes removal work.</summary>
    public static string Section(HeartbeatSkillSet skills)
    {
        var text = new StringBuilder("## Your Heartbeat Skills\n");
        text.Append("Your owner chose these, and this list replaces any earlier one: if an earlier tick told you to do something that isn't here now, stop doing it. Work nobody asked for is only yours to start if a Skill here says so. Never change this list yourself; it is the Heartbeat Skills field on your Agent.\n");
        if (skills.Loaded.Count == 0)
            text.Append("\nYou have none right now.\n");
        foreach (var skill in skills.Loaded)
            text.Append("\n### ").Append(skill.Name).Append('\n').Append(skill.Instructions).Append('\n');
        if (skills.Refused.Count > 0)
        {
            text.Append("\n### Not loaded\nTell your owner about these once, in LIVE, in plain words:\n");
            foreach (var skill in skills.Refused)
                text.Append("- ").Append(skill.Name).Append(": ").Append(skill.Reason).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    private static string Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
