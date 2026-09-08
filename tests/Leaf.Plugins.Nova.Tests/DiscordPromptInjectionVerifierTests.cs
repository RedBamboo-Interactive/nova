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
}
