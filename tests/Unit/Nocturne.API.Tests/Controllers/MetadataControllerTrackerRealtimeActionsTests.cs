using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

[Trait("Category", "Unit")]
public class MetadataControllerTrackerRealtimeActionsTests
{
    [Fact]
    public void GetTrackerRealtimeActions_OffersEveryActionAsItsWireName()
    {
        var metadata = new MetadataController().GetTrackerRealtimeActions()
            .Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrackerRealtimeActionsMetadata>().Subject;

        metadata.AvailableActions
            .Select(action => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(action)))
            .Should().Equal("create", "reopen", "delete", "complete", "ack");
        metadata.Description.Should().NotBeNullOrWhiteSpace();
    }
}
