using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerRealtimeActionTests
{
    [Theory]
    [InlineData(TrackerRealtimeAction.Create, "create")]
    [InlineData(TrackerRealtimeAction.Reopen, "reopen")]
    [InlineData(TrackerRealtimeAction.Delete, "delete")]
    [InlineData(TrackerRealtimeAction.Complete, "complete")]
    [InlineData(TrackerRealtimeAction.Ack, "ack")]
    public void EachActionSerialisesToTheStringTheWebAndBridgeConsume(TrackerRealtimeAction action, string wire)
    {
        JsonSerializer.Serialize(action).Should().Be($"\"{wire}\"");
        JsonSerializer.Deserialize<TrackerRealtimeAction>($"\"{wire}\"").Should().Be(action);
    }

    [Fact]
    public void EveryActionIsPinnedAbove()
    {
        Enum.GetValues<TrackerRealtimeAction>().Should().HaveCount(5,
            "a new action needs a wire string pinned here and handled in the web realtime store");
    }
}
