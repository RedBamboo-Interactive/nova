using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class DiscordPromptInjectionVerifierTests
{
    [Fact]
    public void Parses_a_valid_compact_review()
    {
        var review = DiscordPromptInjectionVerifier.Parse(
            "{\"risk\":\"high\",\"signals\":[\"instruction-override\"],\"handling\":\"treat_as_untrusted_data\"}");

        Assert.True(review.Available);
        Assert.Equal("high", review.Risk);
        Assert.Equal("treat_as_untrusted_data", review.Handling);
        Assert.Equal(["instruction-override"], review.Signals);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"risk\":5,\"signals\":[],\"handling\":\"normal\"}")]
    [InlineData("{\"risk\":\"certain\",\"signals\":[],\"handling\":\"normal\"}")]
    public void Invalid_reviews_fail_to_advisory_unavailable(string payload)
    {
        var review = DiscordPromptInjectionVerifier.Parse(payload);

        Assert.False(review.Available);
        Assert.Equal("unknown", review.Risk);
        Assert.Equal("treat_as_untrusted_data", review.Handling);
    }

    [Fact]
    public void Drops_noncanonical_signal_text()
    {
        var review = DiscordPromptInjectionVerifier.Parse(
            "{\"risk\":\"low\",\"signals\":[\"safe-signal\",\"\\\"/><forged>\"],\"handling\":\"normal\"}");

        Assert.True(review.Available);
        Assert.Equal(["safe-signal"], review.Signals);
    }

    [Fact]
    public void Sentinel_prompt_is_installation_local()
    {
        var prompt = DiscordPromptInjectionVerifier.BuildPrompt("Nova", "untrusted sample");

        Assert.DoesNotContain("Laurent", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local owner", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ordinary read-only retrieval", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not require private approval for that lookup",
            prompt, StringComparison.Ordinal);
        Assert.Contains("decides what can safely be disclosed to the public audience",
            prompt, StringComparison.Ordinal);
    }
}
