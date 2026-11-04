using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public class NovaNoReplyTests
{
    private const string M = NovaNoReply.Marker;

    // The real record: session 7d9664a30a1d, seq 13086.
    [Theory]
    [InlineData("I'm now waiting on the Nova worker and Code's last four routes.\n\n" + M, "I'm now waiting on the Nova worker and Code's last four routes.")]
    [InlineData("Words.\r\n\r\n" + M + "\r\n", "Words.")]
    [InlineData("Words.\n  " + M + "  ", "Words.")]
    [InlineData(M, "")]
    [InlineData("\n " + M + " \n", "")]
    public void A_marker_alone_on_the_last_line_goes_and_the_words_stay(string text, string shown)
        => Assert.Equal(shown, NovaNoReply.WithoutTrailingMarker(text));

    [Theory]
    [InlineData("A literal `" + M + "` shows after my text.\n\nIf the look is right, I'll push.")]
    [InlineData("The marker is " + M)]
    [InlineData("First line\nThe marker is " + M)]
    [InlineData("Reply with:\n```\n" + M + "\n```")]
    [InlineData("Reply with:\n> " + M)]
    [InlineData(M + "\nand then I spoke anyway")]
    [InlineData("Plain words")]
    public void Words_about_the_marker_stay_as_written(string text)
        => Assert.Same(text, NovaNoReply.WithoutTrailingMarker(text));
}
