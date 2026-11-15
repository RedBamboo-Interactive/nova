using Leaf.Plugins.Nova;
using Leaf.Plugins.Nova.Endpoints;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

/// <summary>
/// 2026-11-15: the Fable allowance ran out overnight. The Nature orchestrator (069f571f36a9) and its
/// worker (cd6e22042916) died on the exact message below, and the thread just looked quiet.
/// </summary>
public sealed class ProviderLimitNoticeTests
{
    private const string FableLimit = TurnOutcomeCallbackTests.FableLimit;
    private static readonly DateTimeOffset At = new(2026, 11, 15, 3, 4, 17, TimeSpan.Zero);

    [Theory]
    [InlineData("usage_limit", null, true)]
    [InlineData("authentication", null, true)]
    [InlineData(null, "usage_limit", true)]
    [InlineData(null, "authentication_failed", true)]
    [InlineData("transient", null, false)]
    [InlineData("other", "usage_limit", false)] // RedCompute's error kind wins over a stale stop reason
    [InlineData(null, null, false)]
    [InlineData(null, "completed", false)]
    public void Only_an_account_level_failure_is_a_provider_limit(string? kind, string? stopReason, bool expected)
        => Assert.Equal(expected, ProviderLimitNotice.IsAccountLevel(kind, stopReason));

    [Fact]
    public void The_statement_keeps_the_reset_time_and_drops_the_advice_and_link()
        => Assert.Equal("You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich).", ProviderLimitNotice.Statement(FableLimit));

    [Theory]
    [InlineData("Claude AI usage limit reached|1763150400", "Claude AI usage limit reached|1763150400")]
    [InlineData("  ", "no message from the provider")]
    [InlineData("5-hour limit reached ∙ resets 3pm", "5-hour limit reached ∙ resets 3pm")]
    public void A_statement_without_a_sentence_end_is_kept_whole(string error, string expected)
        => Assert.Equal(expected, ProviderLimitNotice.Statement(error));

    [Fact]
    public void Every_session_on_one_limit_shares_the_episode_and_a_new_reset_or_day_is_a_new_one()
    {
        var key = ProviderLimitNotice.EpisodeKey(FableLimit, At);
        Assert.Equal(key, ProviderLimitNotice.EpisodeKey(FableLimit.Replace("from=cc_cli_limit_message", "from=other"), At.AddHours(2)));
        Assert.Equal(key, ProviderLimitNotice.EpisodeKey("  " + FableLimit.ToUpperInvariant(), At));
        Assert.NotEqual(key, ProviderLimitNotice.EpisodeKey(FableLimit.Replace("Nov 20", "Nov 27"), At));
        Assert.NotEqual(key, ProviderLimitNotice.EpisodeKey(FableLimit, At.AddDays(1)));
        Assert.StartsWith("provider-limit:2026-11-15:", key);
    }

    [Fact]
    public void The_line_says_when_where_and_the_providers_words_in_Zurich_time()
    {
        var line = ProviderLimitNotice.Line("usage_limit", FableLimit, At, "Nature Environment AAA Visual Uplift");
        Assert.Equal("Provider usage limit hit at 04:04 Zurich time (\"Nature Environment AAA Visual Uplift\" stopped on it): "
            + "You've hit your Fable limit · resets Nov 20, 9pm (Europe/Zurich). Sessions on that model fail until it resets; nothing retries them.", line);
        Assert.DoesNotContain("stopped on it", ProviderLimitNotice.Line("usage_limit", FableLimit, At, null));
        Assert.StartsWith("Provider sign-in failed at 04:04", ProviderLimitNotice.Line("authentication", "Invalid API key · Please run /login", At, null));
    }

    [Fact]
    public void The_discussion_projection_keeps_the_error_a_turn_ended_on()
    {
        var error = new ConversationExporter.CollapsedMessage { Role = "assistant", EventType = "error", Content = FableLimit };
        Assert.True(DiscussionEndpoints.IsVisibleSessionMessage(error));
        var json = System.Text.Json.JsonSerializer.Serialize(DiscussionEndpoints.MapSessionMessageParts(error, FableLimit));
        Assert.Contains("\"type\":\"error\"", json);
        Assert.Contains("You\\u0027ve hit your Fable limit", json);

        Assert.False(DiscussionEndpoints.IsVisibleSessionMessage(
            new ConversationExporter.CollapsedMessage { Role = "assistant", EventType = "error", Content = " " }));
    }
}
