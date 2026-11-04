using System.Text.Json.Nodes;
using Leaf.Sdk;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class HeartbeatSkillsTests
{
    private static readonly HeartbeatConfig Config = new(null, null, "deep", 15, 15);

    private static string SeedsPath(params string[] parts) => Path.GetFullPath(Path.Combine(
        [AppContext.BaseDirectory, "..", "..", "..", "..", "..", "seeds", .. parts]));

    private static LeafEntity Skill(string name, string instructions) => new(
        Guid.NewGuid(), HeartbeatSkills.SkillType, name.ToLowerInvariant(), name,
        new JsonObject { ["instructions"] = instructions },
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);

    private static HeartbeatSkillSet Set(params LeafEntity[] skills) => HeartbeatSkills.Select(skills);

    private static string Morning(HeartbeatSkillSet skills, string name = "Nova")
        => HeartbeatPrompts.Morning("digest", Config, name, skills);

    private static string Tick(HeartbeatSkillSet skills) => HeartbeatPrompts.Tick("digest", null, Config, skills);

    [Fact]
    public void Every_tick_carries_a_skill_in_full_not_only_the_morning()
    {
        var skills = Set(Skill("Watering", "Water the plants at noon."));

        foreach (var prompt in new[] { Morning(skills), Tick(skills) })
        {
            Assert.Contains("### Watering", prompt);
            Assert.Contains("Water the plants at noon.", prompt);
        }
    }

    [Fact]
    public void The_list_states_that_it_replaces_earlier_ones_so_taking_one_off_works_mid_day()
    {
        foreach (var prompt in new[] { Morning(HeartbeatSkillSet.Empty), Tick(HeartbeatSkillSet.Empty) })
        {
            Assert.Contains("this list replaces any earlier one", prompt);
            Assert.Contains("You have none right now", prompt);
            Assert.Contains("Work nobody asked for is only yours to start if a Skill here says so", prompt);
        }
    }

    [Fact]
    public void Without_skills_nothing_in_the_prompts_commissions_unrequested_work()
    {
        var prompts = new[] { Morning(HeartbeatSkillSet.Empty), Tick(HeartbeatSkillSet.Empty) };

        foreach (var prompt in prompts)
            foreach (var phrase in new[]
            {
                "Do real work", "nobody's asked you to", "did nothing with it", "Pick something and do it",
                "window for agency", "Initiative:", "Leaf Discord", "Action-item", "Nudge LIVE-me", "Conversation nudge",
            })
                Assert.DoesNotContain(phrase, prompt);
    }

    [Fact]
    public void A_skill_at_the_limit_loads_and_one_past_it_is_refused_with_what_to_do()
    {
        var set = Set(
            Skill("Fits", new string('x', HeartbeatSkills.MaxInstructionLength)),
            Skill("TooLong", new string('x', HeartbeatSkills.MaxInstructionLength + 1)));

        Assert.Equal("Fits", Assert.Single(set.Loaded).Name);
        var refused = Assert.Single(set.Refused);
        Assert.Equal("TooLong", refused.Name);
        Assert.Contains("1501 characters", refused.Reason);
        Assert.Contains("point at a full Skill", refused.Reason);

        var prompt = Tick(set);
        Assert.Contains("Tell your owner about these once", prompt);
        Assert.DoesNotContain(new string('x', HeartbeatSkills.MaxInstructionLength + 1), prompt);
    }

    [Fact]
    public void An_empty_skill_is_refused()
    {
        var set = Set(Skill("Blank", "   "));

        Assert.Empty(set.Loaded);
        Assert.Equal("Blank", Assert.Single(set.Refused).Name);
    }

    [Fact]
    public void The_morning_briefing_addresses_the_agent_it_belongs_to()
    {
        var morning = Morning(HeartbeatSkillSet.Empty, "Leni");

        Assert.Contains("You are Leni.", morning);
        Assert.DoesNotContain("You are Nova", morning);
    }

    [Fact]
    public void Every_default_ships_as_a_seed_small_enough_and_free_of_this_installation()
    {
        foreach (var slug in HeartbeatSkills.DefaultSlugs)
        {
            var markdown = File.ReadAllText(SeedsPath("skills", slug, "SKILL.md")).Replace("\r\n", "\n");
            var body = markdown[(markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..].Trim();

            Assert.StartsWith($"---\nname: {slug}\n", markdown);
            Assert.InRange(body.Length, 1, HeartbeatSkills.MaxInstructionLength);
            Assert.DoesNotContain("Laurent", markdown, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(SeedsPath("skills", slug, "agents", "openai.yaml")));
        }
    }

    [Fact]
    public void The_initiatives_skill_says_how_to_start_one_and_what_bounds_it()
    {
        var markdown = File.ReadAllText(SeedsPath("skills", "heartbeat-initiatives", "SKILL.md"));

        foreach (var phrase in new[]
        {
            "/api/apps/nova/discussions", "Initiative: <what changes>", "idempotencyKey", "Never open one to ask for a yes",
            "One new one per tick", "your identity's limits win", "standing-invitations.md", "no restart, rebuild or release",
        })
            Assert.Contains(phrase, markdown);
    }

    [Fact]
    public void The_seed_defines_the_list_as_a_list_of_skills()
    {
        var fields = JsonNode.Parse(File.ReadAllText(SeedsPath("agent-fields.json")))!.AsArray().OfType<JsonObject>().ToList();
        var field = Assert.Single(fields, f => f["slug"]?.GetValue<string>() == HeartbeatSkills.Field);

        Assert.Equal("agent", field["parentType"]!.GetValue<string>());
        Assert.Equal("entity_list", field["fieldType"]!.GetValue<string>());
        Assert.Contains("agent-skill", field["constraints"]!.GetValue<string>());
        Assert.Contains("1,500", field["description"]!.GetValue<string>());
        Assert.DoesNotContain(fields, f => f["slug"]?.GetValue<string>() == "initiatives");
    }
}
