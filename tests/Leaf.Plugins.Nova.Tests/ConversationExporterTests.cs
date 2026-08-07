using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ConversationExporterTests
{
    [Fact]
    public void StripInjectedTagsRemovesScratchSpaceInstructions()
    {
        const string content = """
            <scratch-space path="S:\Nova\f13bad36">
            Put disposable artifacts here.
            </scratch-space>

            Keep this authored message.
            """;

        var stripped = ConversationExporter.StripInjectedTags(content);

        Assert.Equal("Keep this authored message.", stripped);
    }

    [Fact]
    public void StripInjectedTagsRemovesEveryInternalEnvelope()
    {
        const string content = """
            <nova-context timestamp="now">context</nova-context>
            <nova-prior-message role="assistant">prior</nova-prior-message>
            <scratch-space path="S:\Nova\discussion">scratch</scratch-space>
            Authored text.
            """;

        var stripped = ConversationExporter.StripInjectedTags(content);

        Assert.Equal("Authored text.", stripped);
    }
}
