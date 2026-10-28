using System.Text.Json;
using Leaf.Plugins.Nova;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public class ExternalConversationTimestampTests
{
    private sealed record Wire(DateTime Timestamp);

    [Fact]
    public void OffsetTimestampFromRedComputeKeepsItsInstant()
    {
        // RedCompute's transcript page sends offset timestamps; System.Text.Json reads them as Local.
        var wire = JsonSerializer.Deserialize<Wire>("""{"Timestamp":"2026-10-28T18:45:50.6087484+00:00"}""")!;

        var projected = ExternalAgentConversationProvider.AsUtcOffset(wire.Timestamp);

        Assert.Equal(DateTimeOffset.Parse("2026-10-28T18:45:50.6087484+00:00"), projected);
        Assert.Equal(TimeSpan.Zero, projected.Offset);
    }

    [Fact]
    public void UnspecifiedTimestampIsTreatedAsUtc()
    {
        var projected = ExternalAgentConversationProvider.AsUtcOffset(
            new DateTime(2026, 10, 28, 18, 45, 50, DateTimeKind.Unspecified));

        Assert.Equal(new DateTimeOffset(2026, 10, 28, 18, 45, 50, TimeSpan.Zero), projected);
    }
}
